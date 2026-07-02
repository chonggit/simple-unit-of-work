# UnitOfWork 自动开启事务参数 — 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 为 `UnitOfWork` 构造函数增加 `autoTransaction` 和 `isolationLevel` 参数，支持手动/不开启事务模式，运行时强制"不允许中途开启事务"。

**Architecture:** 在 `UnitOfWorkContext` 中新增 `volatile` 追踪标记，`Connection` getter 在无事务访问时置位；`Demand()` 在 lock 内检查并拒绝中途开启；`Dispose()` 仅在活动事务存在时回滚。API 通过构造函数默认参数保持向后兼容。

**Tech Stack:** C# 12, .NET Standard 2.0 / .NET Framework 4.6.1 / .NET 8.0 / .NET 10.0, Dapper 2.1.79

## Global Constraints

- 目标框架: net461;netstandard2.0;net8.0;net10.0
- `_hasUntransactedAccess` 必须为 `volatile`
- `isolationLevel` 参数始终存入 `_isolationLevel` 字段
- `Create()` 重载需 try-catch 防止连接泄漏
- 所有现有公开 API 保持向后兼容

## File Structure

| 文件 | 操作 | 职责 |
|---|---|---|
| `src/SimpleUnitOfWork/UnitOfWorkContext.cs` | 修改 | 新增 `_hasUntransactedAccess` volatile 字段，`Connection` getter 置位 |
| `src/SimpleUnitOfWork/UnitOfWork.cs` | 修改 | 构造函数参数、Demand 检查、CompleteTransaction 消息、Dispose 安全化、Create 重载 |

不变的文件：`IUnitOfWork.cs`、`IUnitOfWorkContext.cs`、`IRepository.cs`、`Repository.cs`

---

### Task 1: UnitOfWorkContext — 新增追踪标记

**Files:**
- Modify: `src/SimpleUnitOfWork/UnitOfWorkContext.cs`

**Interfaces:**
- Produces: `internal bool HasUntransactedAccess { get; }` — 供 `UnitOfWork.Demand()` 读取
- Produces: `private volatile bool _hasUntransactedAccess;` — 由 `Connection` getter 写入

- [ ] **Step 1: 在 UnitOfWorkContext 中添加字段和属性**

在 `_disposed` 字段附近添加 `_hasUntransactedAccess`：

```csharp
private volatile bool _hasUntransactedAccess;
```

在 `CommandTimeout` 属性之后添加 `HasUntransactedAccess` 属性：

```csharp
/// <summary>
/// 是否在无事务状态下访问过连接。由 Connection getter 自动置位，Demand() 内部检查后重置。
/// </summary>
internal bool HasUntransactedAccess
{
    get => _hasUntransactedAccess;
    set => _hasUntransactedAccess = value;
}
```

- [ ] **Step 2: 修改 Connection getter，在 Transaction == null 时置位**

将现有的：

```csharp
public IDbConnection Connection
{
    get
    {
        if (_isCompleted())
            throw new InvalidOperationException("This UnitOfWork has already been committed or rolled back.");
        return _connection;
    }
}
```

改为：

```csharp
public IDbConnection Connection
{
    get
    {
        if (_isCompleted())
            throw new InvalidOperationException("This UnitOfWork has already been committed or rolled back.");
        if (Transaction == null)
            _hasUntransactedAccess = true;
        return _connection;
    }
}
```

- [ ] **Step 3: 编译验证**

```bash
cd "D:/cihong/github/simple-unit-of-work" && dotnet build src/SimpleUnitOfWork/SimpleUnitOfWork.csproj
```

- [ ] **Step 4: 提交**

```bash
git add src/SimpleUnitOfWork/UnitOfWorkContext.cs
git commit -m "feat: add _hasUntransactedAccess tracking flag to UnitOfWorkContext

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 2: UnitOfWork — 构造函数增加 autoTransaction 和 isolationLevel 参数

**Files:**
- Modify: `src/SimpleUnitOfWork/UnitOfWork.cs`

**Interfaces:**
- Consumes: `UnitOfWorkContext.HasUntransactedAccess` (from Task 1)
- Produces: `public UnitOfWork(IDbConnection, bool autoTransaction = true, IsolationLevel isolationLevel = ReadCommitted)`

- [ ] **Step 1: 修改构造函数签名和实现**

将现有的：

```csharp
private IsolationLevel _isolationLevel = IsolationLevel.ReadCommitted;
```

保持该字段不变。

将现有的构造函数：

```csharp
public UnitOfWork(IDbConnection connection)
{
    if (connection == null)
        throw new ArgumentNullException(nameof(connection), "Connection cannot be null.");
    _context = new UnitOfWorkContext(connection, () => _completed);
}
```

改为：

```csharp
/// <summary>
/// 构造函数，传入可用的数据库连接。
/// </summary>
/// <param name="connection">数据库连接，不能为 null</param>
/// <param name="autoTransaction">是否自动开启事务，默认 true。false 时需要手动调用 Demand() 或完全不使用事务。</param>
/// <param name="isolationLevel">事务隔离级别，始终存入 _isolationLevel 字段。</param>
public UnitOfWork(
    IDbConnection connection,
    bool autoTransaction = true,
    IsolationLevel isolationLevel = IsolationLevel.ReadCommitted)
{
    if (connection == null)
        throw new ArgumentNullException(nameof(connection), "Connection cannot be null.");
    _isolationLevel = isolationLevel;
    _context = new UnitOfWorkContext(connection, () => _completed);
    if (autoTransaction)
    {
        Demand(isolationLevel);
    }
}
```

- [ ] **Step 2: 编译验证**

```bash
cd "D:/cihong/github/simple-unit-of-work" && dotnet build src/SimpleUnitOfWork/SimpleUnitOfWork.csproj
```

- [ ] **Step 3: 提交**

```bash
git add src/SimpleUnitOfWork/UnitOfWork.cs
git commit -m "feat: add autoTransaction and isolationLevel params to UnitOfWork constructor

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 3: UnitOfWork.Demand() — 运行时前置检查 + 重置标记

**Files:**
- Modify: `src/SimpleUnitOfWork/UnitOfWork.cs`

**Interfaces:**
- Consumes: `UnitOfWorkContext.HasUntransactedAccess` (from Task 1)

- [ ] **Step 1: 在 Demand(IsolationLevel) 中加入前置检查和标记重置**

将现有的 `Demand(IsolationLevel level)` 方法：

```csharp
public void Demand(IsolationLevel level)
{
    lock (_lock)
    {
        EnsureNotDisposed();
        if (_completed)
            throw new InvalidOperationException("This UnitOfWork has already been committed or rolled back.");
        if (_context.Transaction != null)
        {
            if (_isolationLevel != level)
                throw new InvalidOperationException(
                    $"事务已存在，等级不匹配：当前为 {_isolationLevel}，传入为 {level}。");
            return;
        }
        _isolationLevel = level;
        EnsureConnectionOpen();
        _context.Transaction = _context.Connection.BeginTransaction(level);
    }
}
```

改为：

```csharp
public void Demand(IsolationLevel level)
{
    lock (_lock)
    {
        EnsureNotDisposed();
        if (_completed)
            throw new InvalidOperationException("This UnitOfWork has already been committed or rolled back.");
        if (_context.Transaction != null)
        {
            if (_isolationLevel != level)
                throw new InvalidOperationException(
                    $"事务已存在，等级不匹配：当前为 {_isolationLevel}，传入为 {level}。");
            return;
        }
        // 检查是否已在无事务状态下执行过操作
        if (_context.HasUntransactedAccess)
            throw new InvalidOperationException(
                "无法开启事务：已有操作在无事务状态下执行。请确保 Demand() 在所有数据库操作之前调用。");
        _isolationLevel = level;
        EnsureConnectionOpen();
        _context.Transaction = _context.Connection.BeginTransaction(level);
        _context.HasUntransactedAccess = false; // 事务已创建，重置标记
    }
}
```

- [ ] **Step 2: 编译验证**

```bash
cd "D:/cihong/github/simple-unit-of-work" && dotnet build src/SimpleUnitOfWork/SimpleUnitOfWork.csproj
```

- [ ] **Step 3: 提交**

```bash
git add src/SimpleUnitOfWork/UnitOfWork.cs
git commit -m "feat: add runtime check in Demand() to prevent mid-operation transaction start

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 4: UnitOfWork.CompleteTransaction() — 区分错误消息

**Files:**
- Modify: `src/SimpleUnitOfWork/UnitOfWork.cs`

**Interfaces:**
- Consumes: `UnitOfWorkContext.HasUntransactedAccess` (from Task 1)

- [ ] **Step 1: 修改 CompleteTransaction 中的错误消息**

将现有的：

```csharp
private void CompleteTransaction(Action<IDbTransaction> action)
{
    EnsureNotDisposed();
    if (_completed)
        throw new InvalidOperationException("This UnitOfWork has already been committed or rolled back.");

    var transaction = _context.Transaction;
    if (transaction == null)
        throw new InvalidOperationException("There is no active transaction to complete. Call Demand() first.");

    try
    {
        action(transaction);
        _completed = true;
    }
    finally
    {
        try { transaction.Dispose(); }
        catch { /* Dispose 失败不能掩饰 commit/rollback 的异常 */ }
        _context.Transaction = null;
    }
}
```

改为：

```csharp
private void CompleteTransaction(Action<IDbTransaction> action)
{
    EnsureNotDisposed();
    if (_completed)
        throw new InvalidOperationException("This UnitOfWork has already been committed or rolled back.");

    var transaction = _context.Transaction;
    if (transaction == null)
    {
        if (_context.HasUntransactedAccess)
            throw new InvalidOperationException(
                "当前工作单元运行在无事务模式下，无需调用 Commit/Rollback。");
        throw new InvalidOperationException(
            "There is no active transaction to complete. Call Demand() first.");
    }

    try
    {
        action(transaction);
        _completed = true;
    }
    finally
    {
        try { transaction.Dispose(); }
        catch { /* Dispose 失败不能掩饰 commit/rollback 的异常 */ }
        _context.Transaction = null;
    }
}
```

- [ ] **Step 2: 编译验证**

```bash
cd "D:/cihong/github/simple-unit-of-work" && dotnet build src/SimpleUnitOfWork/SimpleUnitOfWork.csproj
```

- [ ] **Step 3: 提交**

```bash
git add src/SimpleUnitOfWork/UnitOfWork.cs
git commit -m "feat: improve error message when Commit/Rollback called in no-transaction mode

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 5: UnitOfWork.Dispose() — 无事务时跳过回滚

**Files:**
- Modify: `src/SimpleUnitOfWork/UnitOfWork.cs`

- [ ] **Step 1: 修改 Dispose(bool) 在无事务时跳过回滚**

将现有的：

```csharp
protected virtual void Dispose(bool disposing)
{
    if (!_disposedValue)
    {
        if (disposing)
        {
            try
            {
                Rollback(); // CompleteTransaction 已处理异常
            }
            catch
            {
                // 所有异常在此吞掉 — Dispose 绝不能抛异常
            }
            finally
            {
                _context.Dispose(); // 会释放 Transaction（如未释放）和 Connection
            }
        }
        _disposedValue = true;
    }
}
```

改为：

```csharp
protected virtual void Dispose(bool disposing)
{
    if (!_disposedValue)
    {
        if (disposing)
        {
            // 仅在有活动事务时回滚（Transaction 读取在 Dispose() 的 lock 保护下）
            if (_context.Transaction != null)
            {
                try
                {
                    Rollback(); // CompleteTransaction 已处理异常
                }
                catch
                {
                    // 所有异常在此吞掉 — Dispose 绝不能抛异常
                }
            }
            _context.Dispose(); // 会释放 Transaction（如未释放）和 Connection
        }
        _disposedValue = true;
    }
}
```

- [ ] **Step 2: 编译验证**

```bash
cd "D:/cihong/github/simple-unit-of-work" && dotnet build src/SimpleUnitOfWork/SimpleUnitOfWork.csproj
```

- [ ] **Step 3: 提交**

```bash
git add src/SimpleUnitOfWork/UnitOfWork.cs
git commit -m "fix: skip Rollback in Dispose when no active transaction

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 6: UnitOfWork.Create() — 新增重载 + 连接泄漏防护

**Files:**
- Modify: `src/SimpleUnitOfWork/UnitOfWork.cs`

- [ ] **Step 1: 提取 private helper 方法并新增 Create 重载**

将现有的三个 Create 相关方法（`Create()`, 两个新重载）统一通过 private helper 实现。

当前代码（第44-53行）：

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

替换为：

```csharp
/// <summary>
/// 使用连接工厂创建 UnitOfWork（自动开启事务，ReadCommitted 隔离级别）。
/// </summary>
public static IUnitOfWork Create()
{
    return CreateCore(autoTransaction: true, IsolationLevel.ReadCommitted);
}

/// <summary>
/// 使用连接工厂创建 UnitOfWork，指定是否自动开启事务。
/// </summary>
public static IUnitOfWork Create(bool autoTransaction)
{
    return CreateCore(autoTransaction, IsolationLevel.ReadCommitted);
}

/// <summary>
/// 使用连接工厂创建 UnitOfWork，指定是否自动开启事务及隔离级别。
/// </summary>
public static IUnitOfWork Create(bool autoTransaction, IsolationLevel isolationLevel)
{
    return CreateCore(autoTransaction, isolationLevel);
}

/// <summary>
/// 核心工厂方法：创建连接、构造 UnitOfWork，构造函数失败时释放连接以防泄漏。
/// </summary>
private static IUnitOfWork CreateCore(bool autoTransaction, IsolationLevel isolationLevel)
{
    var factory = _connectionFactory;
    if (factory == null)
        throw new InvalidOperationException("Connection factory is not set. Call SetConnectionFactory first.");
    var connection = factory();
    if (connection == null)
        throw new InvalidOperationException("Connection factory returned null.");
    try
    {
        return new UnitOfWork(connection, autoTransaction, isolationLevel);
    }
    catch
    {
        // 构造函数失败（如 autoTransaction=true 时 Demand() 抛异常），释放连接
        try { connection.Dispose(); }
        catch { /* Dispose 失败不抛异常 */ }
        throw;
    }
}
```

- [ ] **Step 2: 编译验证**

```bash
cd "D:/cihong/github/simple-unit-of-work" && dotnet build src/SimpleUnitOfWork/SimpleUnitOfWork.csproj
```

- [ ] **Step 3: 提交**

```bash
git add src/SimpleUnitOfWork/UnitOfWork.cs
git commit -m "feat: add Create() overloads with connection leak protection

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 7: 完整编译 + 最终验证

- [ ] **Step 1: 针对所有目标框架编译**

```bash
cd "D:/cihong/github/simple-unit-of-work" && dotnet build src/SimpleUnitOfWork/SimpleUnitOfWork.csproj --configuration Release
```

Expected: 所有 4 个目标框架编译成功。

- [ ] **Step 2: 检查 Diff 确认无遗漏**

```bash
cd "D:/cihong/github/simple-unit-of-work" && git diff HEAD~6..HEAD --stat
```

- [ ] **Step 3: 提交（如有修正）**

```bash
git add -A && git commit -m "chore: final verification after all changes" || echo "No changes to commit"
```
