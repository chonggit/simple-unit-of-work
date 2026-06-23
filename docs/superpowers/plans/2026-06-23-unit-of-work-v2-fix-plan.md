# UnitOfWork 二次修复 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 修复 `UnitOfWork.cs` 中代码审查发现的 13 项缺陷（线程安全、_completed 语义、Dispose 加固、异常安全等）

**Architecture:** 仅改 `src/SimpleUnitOfWork/UnitOfWork.cs`。核心修正是：抽取 `CompleteTransaction(Action)` 消除 Commit/Rollback 重复、加 `lock` 和 `volatile` 实现线程安全、Dispose 路径用外层 try-finally 保证连接释放、逐项修补剩余小问题。

**Tech Stack:** .NET 8, C#, Dapper.Contrib

**改动文件一览：**

| 文件 | 操作 | 说明 |
|------|------|------|
| `src/SimpleUnitOfWork/UnitOfWork.cs` | 修改 | 线程安全 + 重构 + 加固，~30 处变更 |

---

### Task 1: 线程安全基础设施 + 静态方法修补

**Files:**
- Modify: `src/SimpleUnitOfWork/UnitOfWork.cs:10-20`（字段声明）
- Modify: `src/SimpleUnitOfWork/UnitOfWork.cs:26-29`（SetHandleException）
- Modify: `src/SimpleUnitOfWork/UnitOfWork.cs:45-51`（Create）

- [ ] **Step 1: 字段声明加 volatile + 新增 _lock**

将第 10-20 行的字段声明改为：

```csharp
private volatile bool _disposedValue;

private volatile bool _completed;

private IDbTransaction? _transaction;

private IDbConnection _connection;

private static volatile Func<IDbConnection>? _connectionFactory;

private static volatile Action<Exception>? _handleException;

private readonly object _lock = new();
```

变化：
- `_disposedValue` 和 `_completed` 加 `volatile`
- `_connectionFactory` 和 `_handleException` 加 `volatile`
- 新增 `private readonly object _lock = new();`

- [ ] **Step 2: SetHandleException 加 null guard**

将第 26-29 行改为：

```csharp
public static void SetHandleException(Action<Exception> handler)
{
    ArgumentNullException.ThrowIfNull(handler);
    _handleException = handler;
}
```

- [ ] **Step 3: Create() 加工厂返回值 null 检查**

将第 45-51 行的 `Create()` 方法改为：

```csharp
public static IUnitOfWork Create()
{
    var factory = _connectionFactory;
    if (factory == null)
        throw new InvalidOperationException("Connection factory is not set. Call SetConnectionFactory first.");
    var connection = factory();
    if (connection == null)
        throw new InvalidOperationException("Connection factory returned null.");
    return new UnitOfWork(connection);
}
```

- [ ] **Step 4: 验证编译**

Run:
```
cd D:\cihong\github\simple-unit-of-work && dotnet build src/SimpleUnitOfWork/SimpleUnitOfWork.csproj
```
Expected: 编译成功

- [ ] **Step 5: 提交**

```bash
git add src/SimpleUnitOfWork/UnitOfWork.cs
git commit -m "fix: add volatile, _lock field, and null guards for static methods

- _disposedValue, _completed, _connectionFactory, _handleException → volatile
- Add _lock instance for thread safety
- SetHandleException: add ArgumentNullException guard
- Create(): validate factory return value is non-null

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 2: 抽取 CompleteTransaction + 重构 Demand/Commit/Rollback

**Files:**
- Modify: `src/SimpleUnitOfWork/UnitOfWork.cs`（需要在类中添加 EnsureConnectionOpen 和 CompleteTransaction 方法，修改 Demand、Commit、Rollback）

- [ ] **Step 1: 添加 EnsureConnectionOpen 辅助方法**

在 `EnsureNotDisposed()` 方法之后、`Transaction` getter 之前插入：

```csharp
/// <summary>
/// 确保连接处于 Open 状态。处理 ConnectionState.Broken 等异常状态。
/// </summary>
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

- [ ] **Step 2: 在 Commit 方法之前插入 CompleteTransaction 方法**

在 `Dispose` 相关方法之前、`Demand()` 方法之后插入 `CompleteTransaction` 辅助方法：

```csharp
/// <summary>
/// 执行事务提交/回滚操作的公共骨架。处理 _completed 守卫、异常处理、资源清理。
/// 调用前调用者应已持有 _lock。
/// ⚠ _handleException 在实例锁（_lock）内执行，handler 应避免阻塞或调用此实例的方法。
/// </summary>
private void CompleteTransaction(Action<IDbTransaction> action)
{
    EnsureNotDisposed();
    if (_completed)
        throw new InvalidOperationException("This UnitOfWork has already been committed or rolled back.");

    if (_transaction == null)
        throw new InvalidOperationException("There is no active transaction to complete. Call Demand() first.");

    try
    {
        action(_transaction);
        _completed = true;        // 仅在成功后设置，允许失败后重试
    }
    catch (Exception ex)
    {
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

- [ ] **Step 3: 重写 Demand(IsolationLevel)**

将第 105-116 行替换为：

```csharp
public void Demand(IsolationLevel level)
{
    lock (_lock)
    {
        EnsureNotDisposed();
        if (_completed)
            throw new InvalidOperationException("This UnitOfWork has already been committed or rolled back.");
        if (_transaction == null)
        {
            EnsureConnectionOpen();
            _transaction = _connection.BeginTransaction(level);
        }
    }
}
```

变化：
- 外层加 `lock (_lock)`
- `_connection.Open()` 替换为 `EnsureConnectionOpen()`
- `_completed` 守卫（原来已有，保留）
- `EnsureNotDisposed()`（原来已有，保留）

- [ ] **Step 4: 重写 Commit()**

将第 129-150 行替换为：

```csharp
public void Commit()
{
    lock (_lock)
    {
        CompleteTransaction(t => t.Commit());
    }
}
```

- [ ] **Step 5: 重写 Rollback()**

将第 155-176 行替换为：

```csharp
public void Rollback()
{
    lock (_lock)
    {
        CompleteTransaction(t => t.Rollback());
    }
}
```

- [ ] **Step 6: 验证编译**

Run:
```
cd D:\cihong\github\simple-unit-of-work && dotnet build src/SimpleUnitOfWork/SimpleUnitOfWork.csproj
```
Expected: 编译成功，0 warnings

- [ ] **Step 7: 提交**

```bash
git add src/SimpleUnitOfWork/UnitOfWork.cs
git commit -m "refactor: extract CompleteTransaction helper and fix Demand/Commit/Rollback

- Add EnsureConnectionOpen(): handles Broken→Close→Open sequence
- Add CompleteTransaction(Action): unified commit/rollback skeleton
  - _completed set only on success (enables retry on failure)
  - _handleException invocation with inner try-catch (preserves original exception)
  - _transaction.Dispose() with inner try-catch (swallows dispose failure)
  - _transaction == null now throws instead of silent no-op
- Demand(level): add lock, use EnsureConnectionOpen()
- Commit/Rollback: delegate to CompleteTransaction under lock

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 3: Transaction/Connection/Dispose 修复

**Files:**
- Modify: `src/SimpleUnitOfWork/UnitOfWork.cs`（Transaction getter、Connection getter、Dispose(bool)、Dispose()）

- [ ] **Step 1: Transaction getter 加锁**

将第 83-91 行（Transaction getter）改为：

```csharp
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

- [ ] **Step 2: Connection getter 加 _completed 守卫**

将第 56-64 行（Connection getter）改为：

```csharp
public IDbConnection Connection
{
    get
    {
        EnsureNotDisposed();
        if (_completed)
            throw new InvalidOperationException("This UnitOfWork has already been committed or rolled back.");
        return _connection;
    }
}
```

- [ ] **Step 3: 重写 Dispose(bool)**

将第 181-201 行替换为：

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

变化：
- 外层 outer try → catch → finally 结构
- `_connection?.Dispose()` → `_connection.Dispose()`（去掉 `?.`，构造保证非 null）
- `_connection.Dispose()` 移至 finally 块，保证始终执行

- [ ] **Step 4: Dispose() 入口加锁**

将第 214-219 行替换为：

```csharp
public void Dispose()
{
    lock (_lock)
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
```

> 可重入性说明：C# `lock`（Monitor）支持可重入。路径 `Dispose()` → `lock` → `Dispose(bool)` → `Rollback()` → `lock`（同一线程，安全）。

- [ ] **Step 5: 验证编译**

Run:
```
cd D:\cihong\github\simple-unit-of-work && dotnet build src/SimpleUnitOfWork/SimpleUnitOfWork.csproj
```
Expected: 编译成功，0 warnings

- [ ] **Step 6: 提交**

```bash
git add src/SimpleUnitOfWork/UnitOfWork.cs
git commit -m "fix: add lock to Transaction/Dispose, _completed guard to Connection, harden Dispose path

- Transaction getter: wrap body in lock
- Connection getter: add _completed guard (consistent with Transaction)
- Dispose(bool): restructure with try-catch-finally, _connection.Dispose() in finally
- Dispose(): add lock entry, reentrant via Rollback->CompleteTransaction
- _connection.Dispose(): remove unnecessary null-conditional (construction guarantees non-null)

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 4: 最终验证

- [ ] **Step 1: 完整编译**

Run:
```
cd D:\cihong\github\simple-unit-of-work && dotnet build src/SimpleUnitOfWork/SimpleUnitOfWork.csproj
```
Expected: 编译成功，0 warnings

- [ ] **Step 2: 审查最终改动**

```bash
cd D:\cihong\github\simple-unit-of-work && git diff HEAD~3..HEAD
```

逐行审查确认：
- `_completed` 仅在 `CompleteTransaction` try 块中置 true
- 所有公共方法入口有 `lock`（除 `Connection` getter）
- `Dispose(bool)` finally 块放 `_connection.Dispose()`
- `_transaction == null` 时 `CompleteTransaction` 抛异常
- `_handleException` 调用被内层 try-catch 包裹
- `_transaction.Dispose()` 被内层 try-catch 包裹
- `EnsureConnectionOpen()` 处理 Broken → Close → Open
- `SetHandleException` 有 `ArgumentNullException.ThrowIfNull`
- `Create()` 验证工厂返回值不为 null
