# UnitOfWork 修复设计

## 背景

code review 发现 `SimpleUnitOfWork` 库中有多个缺陷，最严重的是 `Demand(IsolationLevel)` 中通过 `Connection` 属性（getter 又调 `Demand()`）而非 `_connection` 字段访问连接，导致无限递归/StackOverflowException，库完全不可用。

同时存在 Dispose 中泄漏事务、异常安全缺失、构造函数缺少参数校验等问题。

## 修复范围

只修 bug 和低成本加固项，不动未引起实际问题的设计取舍。

### 在范围内

| # | 问题 | 文件 | 行 |
|---|------|------|---|
| 1 | Connection 属性 getter 调 Demand() 导致递归崩溃 | UnitOfWork.cs | 61 |
| 2 | Dispose() 中访问 Connection 属性再次触发 Demand() 泄漏事务 | UnitOfWork.cs | 112 |
| 3 | Dispose(bool) 无 try-catch，Rollback 抛异常阻断了 Connection.Dispose() | UnitOfWork.cs | 110 |
| 4 | Commit/Rollback 抛异常后 _transaction 未置空导致工作单元卡死 | UnitOfWork.cs | 80 |
| 5 | Commit 后静默创建新事务导致后续工作被 Dispose 中 Rollback 回滚 | UnitOfWork.cs | 78 |
| 6 | Connection 属性 getter 含副作用（违反 CA1024） | UnitOfWork.cs | 23 |
| 7 | 构造函数缺 ArgumentNullException（UnitOfWork + Repository） | UnitOfWork.cs:49, Repository.cs:16 |
| 8 | 未自动 Open 连接 | UnitOfWork.cs | 51 |
| 9 | Dispose 后没有 ObjectDisposedException 防护 | UnitOfWork.cs | 103 |

### YAGNI 排除（现在不动）

- CancellationToken 支持 — 调用者未要求
- ConfigureAwait(false) — 需要时再加
- 线程安全 — 工作单元不跨线程共享
- IRepository `class, new()` 约束泄漏 — 不影响使用
- 只读操作免事务 — 设计取舍

## 核心思路

所有 bug 的根因是 `Connection` 属性 getter 中调用了 `Demand()`（创建事务）。修复方案：

1. **Connection getter 去掉 Demand()** — 恢复为纯属性 getter
2. **Demand(level) 改用 _connection 字段** — 不再通过 getter 递归访问
3. **Dispose(bool) 改用 _connection 字段** — 不再触发 Demand()
4. **Commit/Rollback 加 try-finally** — 确保 _transaction 始终被清理
5. **Dispose 加 try-catch** — 确保不抛异常
6. **公共入口加 ObjectDisposedException 检查**
7. **构造函数加 null 检查**
8. **Demand 中自动 Open 连接**

## 改动文件

仅改 2 个文件，不涉及接口变更：

| 文件 | 改动量 | 说明 |
|------|--------|------|
| `UnitOfWork.cs` | ~15 处 | 核心修复，追加防护代码 |
| `Repository.cs` | 1 处 | 构造函数加 ArgumentNullException |

## 详细设计

### 1. Connection 属性 getter 去掉 Demand()

**改前：**
```csharp
public IDbConnection Connection
{
    get
    {
        Demand();
        return _connection;
    }
}
```

**改后：**
```csharp
public IDbConnection Connection
{
    get
    {
        EnsureNotDisposed();
        return _connection;
    }
}
```

- Demand() 只由 Transaction getter 调用
- Connection 变为纯 getter，仅加 disposed 防护

### 2. Demand(level) 改用 _connection 字段

**改前：**
```csharp
_transaction = Connection.BeginTransaction(level);
```

**改后：**
```csharp
if (_connection.State != ConnectionState.Open)
    _connection.Open();
_transaction = _connection.BeginTransaction(level);
```

- 用 `_connection` 避免重新进入 Connection getter → Demand() 循环
- 自动 Open 连接

### 3. Dispose(bool) 改用 _connection 字段 + try-catch

**改前：**
```csharp
Rollback();
if (Connection != null)
{
    Connection.Dispose();
}
```

**改后：**
```csharp
try
{
    Rollback();
}
catch
{
    // Dispose 绝不能抛异常
}

_connection?.Dispose();
```

### 4. Commit/Rollback try-finally

**改前：**
```csharp
public void Commit()
{
    if (_transaction != null)
    {
        _transaction.Commit();
        _transaction.Dispose();
        _transaction = null;
    }
}
```

**改后：**
```csharp
public void Commit()
{
    EnsureNotDisposed();
    if (_transaction != null)
    {
        try
        {
            _transaction.Commit();
        }
        finally
        {
            _transaction.Dispose();
            _transaction = null;
        }
    }
}
```

Rollback 同理。

### 5. ObjectDisposedException 防护

```csharp
private void EnsureNotDisposed()
{
    if (_disposedValue)
        throw new ObjectDisposedException(nameof(UnitOfWork));
}
```

在 Connection、Transaction、Commit、Rollback、Demand 入口调用。

### 6. 构造函数 null 检查

```csharp
// UnitOfWork
public UnitOfWork(IDbConnection connection)
{
    ArgumentNullException.ThrowIfNull(connection);
    _connection = connection;
}

// Repository
public Repository(IUnitOfWork unitOfWork)
{
    ArgumentNullException.ThrowIfNull(unitOfWork);
    _unitOfWork = unitOfWork;
}
```

## 接口兼容性

- `IUnitOfWork` 接口不变
- `IRepository<T>` 接口不变
- 所有公开签名不变（构造函数参数类型不变，只是加了 guard）
- 行为变化：不再调用 `Demand()` 当副作用。调用者如果依赖了 `.Connection` 的副作用（即访问 Connection 自动创建事务），需要改为显式调用 `.Transaction` 或 `.Demand()`。这是对的，因为依赖副作用的调用者本就有 bug。

## 测试策略

- 无现有测试
- 不改测试基础设施（YAGNI：不另建测试项目）
- 改后手动在本地运行确认编译通过

## 错误处理总结

| 场景 | 行为 |
|------|------|
| 传入 null 连接 | 构造时抛 ArgumentNullException |
| 传入 null IUnitOfWork | 构造时抛 ArgumentNullException |
| 连接未 Open | Demand 中自动 Open |
| Commit 失败（死锁等） | 抛异常，_transaction 被 finally 清理，后续可重试 |
| Rollback 失败 | 抛异常，_transaction 被 finally 清理 |
| Dispose 中 Rollback 失败 | 吞异常，_connection 仍被释放 |
| Dispose 后继续用 | 抛 ObjectDisposedException |
| 两次 Dispose | 第二次不执行（_disposedValue 防护） |
