# UnitOfWork 二次修复设计（代码审查跟进）

> 基于 2026-06-23 代码审查报告的 15 项发现，在已完成首次修复基础上进行第二轮修复

## 背景

首次修复（commits `59dda11`..`c33ca1d`）修复了无限递归崩溃、事务泄漏等 9 个缺陷。第二轮代码审查发现 15 项新问题，其中 13 项在 UnitOfWork.cs 层面需修复，2 项 Repository 层面按 YAGNI 排除。

### 修复范围

| # | 问题 | 严重度 | 类型 |
|---|------|--------|------|
| 1 | `_completed` 在 `finally` 中设置，失败后无法重试 | Critical | 行为正确性 |
| 2 | `_handleException` 在 `catch` 内抛出压制原异常 | Critical | 异常安全 |
| 3 | `Dispose` 中 `_handleException` 抛出跳过 `_connection.Dispose()` | Critical | 资源泄漏 |
| 4 | `ConnectionState.Broken` 未处理 | Critical | 兼容性 |
| 5 | 并发 `Demand()` 调用竞争 `_transaction` | Critical | 线程安全 |
| 6 | `_transaction.Dispose()` 在 `finally` 中抛出损坏状态 | Critical | 异常安全 |
| 7 | `_handleException` 在 Dispose 路径中被调用两次 | High | 行为正确性 |
| 8 | `Create()` 未验证工厂返回 null | High | 防御性编程 |
| 9 | `Commit/Rollback` 完成后静默无操作 | High | 行为正确性 |
| 12 | `SetHandleException` 缺 null guard | Medium | API 一致性 |
| 13 | `Connection` getter 缺 `_completed` 守卫 | Medium | API 一致性 |
| 14 | Repository 求值顺序依赖（误报，仅需注释） | Low | 可维护性 |
| 15 | 静态字段无同步 | Medium | 线程安全 |

> **设计决策说明：** v1 设计（`2026-06-23-unit-of-work-fix-design.md`）曾将"线程安全 — 工作单元不跨线程共享"列为 YAGNI 排除。v2 代码审查发现的两项线程安全问题（#5 并发 Demand、#15 静态字段 race）属于数据竞争导致资源泄漏的真实 bug，而非防御性加固，因此本设计推翻 v1 的 YAGNI 排除，纳入修复范围。

### YAGNI 排除

- Repository 读方法强制创建事务（#10）— 设计取舍，保留
- Repository async 缺 `ConfigureAwait(false)`（#11）— 需要时再加
- 其余 refactoring — 不做离题改动

## 核心思路

所有 13 项修复归于 4 个机制：

1. **抽取 `CompleteTransaction` 辅助方法** — 消除 Commit/Rollback 98% 重复，集中处理 `_completed`、`_handleException`、`_transaction.Dispose()`
2. **Dispose 路径加固** — 外层 try-finally 保证 `_connection.Dispose()` 始终执行
3. **线程安全** — 实例 `lock` + 静态 `volatile`
4. **小问题逐项修补** — Broken 处理、null guard、工厂验证

## 改动文件

仅改 1 个文件，无接口变更：

| 文件 | 改动量 | 说明 |
|------|--------|------|
| `src/SimpleUnitOfWork/UnitOfWork.cs` | ~20 处净增/改 | 结构化重构 + 防护加固 |

## 详细设计

### 1. 抽取 `CompleteTransaction(Action)` 辅助方法

**动机：** Commit/Rollback 方法体 98% 相同，差异仅 `.Commit()` vs `.Rollback()`。抽成公共方法可消除重复，并集中处理边缘情况。

```csharp
private void CompleteTransaction(Action<IDbTransaction> action)
{
    EnsureNotDisposed();
    if (_completed)
        throw new InvalidOperationException(
            "This UnitOfWork has already been committed or rolled back.");

    if (_transaction == null)
        throw new InvalidOperationException(
            "There is no active transaction to complete. Call Demand() first.");

    try
    {
        action(_transaction);
        _completed = true;        // 仅在成功后设置，允许失败后重试
    }
    catch (Exception ex)
    {
        // ⚠ _handleException 在实例锁（_lock）内执行。
        // handler 应避免阻塞操作、避免调用此 UnitOfWork 实例的方法（可能死锁）。
        // handler 实现者需保证自己的线程安全（可能被多线程同时调用）。
        try { _handleException?.Invoke(ex); }
        catch { /* handler 异常不压制原始异常 */ }
        throw;                     // 保留原始异常堆栈
    }
    finally
    {
        try { _transaction.Dispose(); }
        catch { /* Dispose 失败不能掩饰 commit/rollback 的异常 */ }
        _transaction = null;
    }
}
```

然后 Commit/Rollback 简化为一行委托传递：

```csharp
public void Commit()   => CompleteTransaction(t => t.Commit());
public void Rollback() => CompleteTransaction(t => t.Rollback());
```

#### 错误处理保证

| 场景 | _completed | _transaction 状态 | 异常 |
|------|-----------|-------------------|------|
| action 成功 | true | null（已 dispose） | 无 |
| action 失败 + handler 成功 | false | null（已 dispose） | 原始异常抛出 |
| action 失败 + handler 失败 | false | null（已 dispose） | 原始异常抛出 |
| _transaction.Dispose() 失败 | 见上 | null | 原始异常/无异常 |
| 未调 Demand() 直接调 Commit/Rollback | false | null | InvalidOperationException |

> **设计说明：** `_transaction == null` 时不再静默返回。调用 Commit()/Rollback() 前必须先通过 `Demand()` 或 `Transaction` getter 创建事务。失败重试流程：`Demand()` → `Commit()` / `Rollback()`。

### 2. Dispose 路径加固

**问题：** 当前 Dispose 的 catch 重复调用 `_handleException`（Rollback 内部已调过），且 handler 抛出会跳过 `_connection.Dispose()`。

**改后：**

```csharp
protected virtual void Dispose(bool disposing)
{
    if (!_disposedValue)
    {
        if (disposing)
        {
            try
            {
                Rollback();           // CompleteTransaction 已处理异常
            }
            catch
            {
                // 所有异常在此吞掉 — Dispose 绝不能抛异常
                // _handleException 已在 Rollback 内被调用
            }
            finally
            {
                _connection.Dispose();  // 构造函数保证 _connection 不为 null
            }
        }
        _disposedValue = true;
    }
}
```

#### 异常路径总结

| 场景 | 行为 |
|------|------|
| Rollback 成功 | 正常释放 _connection |
| Rollback 失败 + handler 被调 | catch 吞掉，finally 释放 _connection |
| Rollback 失败 + handler 也抛 | handler 异常被 CompleteTransaction 吞掉，原始异常被 catch 吞掉，finally 释放 _connection |

### 3. 线程安全

**设计原则：** 工作单元不推荐跨线程共享，但库应防止数据竞争导致的资源泄漏和崩溃。

#### 实例字段加 volatile

`_completed` 在 `CompleteTransaction` 中（锁内）写入，在 `Connection` getter 中（无锁）读取。`_disposedValue` 在 `EnsureNotDisposed()` 中（无锁路径）读取，其写入发生在 `Dispose(bool)` 内。两者均声明为 `volatile`：

```csharp
private volatile bool _disposedValue;
private volatile bool _completed;
```

#### 实例方法锁

添加 `private readonly object _lock = new();`，所有公共 API 入口加锁：

```csharp
// Demand — 事务创建
public void Demand(IsolationLevel level)
{
    lock (_lock)
    {
        EnsureNotDisposed();
        if (_completed)
            throw new InvalidOperationException("...");
        if (_transaction == null)
        {
            EnsureConnectionOpen();
            _transaction = _connection.BeginTransaction(level);
        }
    }
}

// Commit / Rollback — 通过 CompleteTransaction 集中处理
public void Commit()   { lock (_lock) { CompleteTransaction(t => t.Commit()); } }
public void Rollback() { lock (_lock) { CompleteTransaction(t => t.Rollback()); } }

// Transaction getter
public IDbTransaction Transaction
{
    get
    {
        lock (_lock)
        {
            EnsureNotDisposed();
            Demand();
            return _transaction!;
        }
    }
}
```

注意：`Connection` getter 不加锁（纯字段读，`_connection` 构造后不变）。`_completed` 声明为 `volatile`，在无锁读取时保证可见性。`Dispose()` 入口加锁。

> **可重入锁约束：** `Dispose()` 取 `lock` → `Dispose(bool)` → `Rollback()` → `CompleteTransaction()` 路径依赖 C# `lock`（基于 Monitor）的可重入性。若后续改为 `SemaphoreSlim`、`SpinLock` 等不可重入原语，此路径会死锁。如需切换同步原语，必须将 `_connection?.Dispose()` 等关键清理操作提到锁外或改造为无重入架构。

#### 静态字段 volatile

```csharp
private static volatile Func<IDbConnection>? _connectionFactory;
private static volatile Action<Exception>? _handleException;
```

`volatile` 保证 ARM/x86 上所有线程读到最新值，消除 SET 后 GET 仍为空的风险。

> **`_handleException` 线程安全约定：** 引入线程安全后，`_handleException` 委托可能被多线程并发调用（例如两个线程同时 Commit 失败）。`SetHandleException` 的调用者有责任保证传入的 handler 是线程安全的——或者无副作用，或者使用自己的同步机制。

#### 线程安全边界

| 方法 | 锁保护 | 理由 |
|------|--------|------|
| Demand(level) | ✅ _lock | _transaction 创建 |
| Demand() | 通过转调 Demand(level) 保护 | 无 |
| Commit() | ✅ _lock | _completed / _transaction |
| Rollback() | ✅ _lock | _completed / _transaction |
| Transaction { get } | ✅ _lock | 间接调用 Demand |
| Connection { get } | ❌ 无锁 | 纯字段读 |
| Dispose(bool) | ❌ 无锁 | 由 Dispose() 保护 |
| Dispose() | ✅ _lock | 入口加锁 |
| EnsureNotDisposed | ❌ 无锁 | 内部辅助方法 |
| SetConnectionFactory | ❌ 无锁 | volatile 提供可见性 |
| SetHandleException | ❌ 无锁 | volatile 提供可见性 |
| Create | ❌ 无锁 | volatile 提供可见性 |

### 4. 小问题逐项修补

#### 4a. ConnectionState.Broken 处理

在 `Demand(level)` 中，Open 连接前检查 Broken 状态：

```csharp
private void EnsureConnectionOpen()
{
    if (_connection.State == ConnectionState.Open)
        return;

    if (_connection.State == ConnectionState.Broken)
    {
        try { _connection.Close(); }
        catch { /* Close() 在部分 ADO.NET 实现中对 Broken 连接可能抛出。吞掉后尝试 Open() */ }
    }
    _connection.Open();
}
```

调用位置在 `Demand(level)` 中原 `_connection.Open()` 处替换为 `EnsureConnectionOpen()`。

> **关于 ConnectionState 枚举的其他值：** 本方法只处理 `Open` 和 `Broken`。`Connecting`(2)、`Executing`(4)、`Fetching`(8) 是瞬态值，连接处于这些状态时说明外部操作正在进行中——此时调用 `Open()` 由底层 ADO.NET 驱动决定行为（多数驱动抛 `InvalidOperationException`）。调用者应避免在连接有未完成任务时调用 `Demand()`。

#### 4b. Create() 工厂返回 null 检查

```csharp
public static IUnitOfWork Create()
{
    var factory = _connectionFactory;
    if (factory == null)
        throw new InvalidOperationException(
            "Connection factory is not set. Call SetConnectionFactory first.");
    var connection = factory();
    if (connection == null)
        throw new InvalidOperationException(
            "Connection factory returned null.");
    return new UnitOfWork(connection);
}
```

#### 4c. SetHandleException 加 null guard

```csharp
public static void SetHandleException(Action<Exception> handler)
{
    ArgumentNullException.ThrowIfNull(handler);
    _handleException = handler;
}
```

#### 4d. Connection getter 加 _completed 守卫

```csharp
public IDbConnection Connection
{
    get
    {
        EnsureNotDisposed();
        if (_completed)
            throw new InvalidOperationException(
                "This UnitOfWork has already been committed or rolled back.");
        return _connection;
    }
}
```

与 `Transaction` getter 一致的合同（Dispose 后 + 完成后都抛异常）。

### 5. 接口兼容性

- `IUnitOfWork` 接口不变
- `IRepository<T>` 接口不变（本修复不修改）
- `UnitOfWork` 所有公开签名不变（行为变化见下表）
- `Repository<T>` 不变（仅加注释说明）

### 行为变化总结

| 场景 | 改前 | 改后 |
|------|------|------|
| Commit 后重试 | `_completed = true` 锁定，不能重试 | `_completed` 仅成功后置 true，失败可重试 |
| Commit 失败后再 Commit | 重试 (silent no-op, _transaction == null) | 抛 `InvalidOperationException`，"没有活跃事务，先调 Demand()" |
| Commit 后再次 Commit | 静默无操作 | 抛 `InvalidOperationException` |
| Rollback 后 Commit | 静默无操作 | 抛 `InvalidOperationException` |
| Commit 失败后正确重试流程 | 无此语义 | 捕获异常 → `Demand()` → 重新执行工作 → `Commit()` |
| _handleException 在 catch 内抛 | 压制原始异常 | handler 异常被吞，原始异常保留 |
| Dispose 时 Rollback 失败 + handler 被调 | handler 调两次 | handler 仅被 Rollback 调用一次 |
| Dispose 时 _connection.Dispose() | 若 handler 抛则跳过 | finally 块保证执行 |
| 连接是 Broken 状态 | `Open()` 抛 `InvalidOperationException` | 先 `Close()` 再 `Open()` 修复 |
| SetHandleException(null) | 静默清空 handler | 抛 `ArgumentNullException` |
| Create() 工厂返回 null | 构造时抛（误指 connection 为 null） | 抛明确消息的 `InvalidOperationException` |
| 并发 Demand | 两个线程都 `BeginTransaction` 泄漏 | 锁串行化，第二个线程不重复创建 |
| 静态字段跨线程读取 | 可能读到 stale null | volatile 保证可见性 |
| Connection 在 Commit 后访问 | 返回已打开的连接 | 抛 `InvalidOperationException` |

### 测试策略

- 不新建测试项目（YAGNI）
- 改后手动编译确认
- `dotnet build src/SimpleUnitOfWork/SimpleUnitOfWork.csproj`
