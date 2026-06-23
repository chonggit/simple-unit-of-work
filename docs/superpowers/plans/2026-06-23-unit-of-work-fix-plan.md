# UnitOfWork 缺陷修复 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 修复 `UnitOfWork.cs` 中的递归崩溃、事务泄漏、异常安全缺失等 9 个缺陷

**Architecture:** 仅改 `UnitOfWork.cs`（~15 处）和 `Repository.cs`（1 处）。核心修正是将 `Demand(level)` 内访问 `Connection` 属性改为访问 `_connection` 字段，去掉 `Connection` getter 中的 `Demand()` 副作用，追加 try-finally/try-catch 防护。

**Tech Stack:** .NET 8, C#, Dapper.Contrib

**改动文件一览:**

| 文件 | 操作 | 说明 |
|------|------|------|
| `src/SimpleUnitOfWork/UnitOfWork.cs` | 修改 | 根因修复 + 防护代码 |
| `src/SimpleUnitOfWork/Repository.cs` | 修改 | 构造函数加 null guard |

---

### Task 1: 修复 UnitOfWork 构造函数 + 添加 EnsureNotDisposed

**Files:**
- Modify: `src/SimpleUnitOfWork/UnitOfWork.cs:49-52`

- [ ] **Step 1: 构造函数加 ArgumentNullException**

当前代码（第 49-52 行）：
```csharp
public UnitOfWork(IDbConnection connection)
{
    _connection = connection;
}
```

改为：
```csharp
public UnitOfWork(IDbConnection connection)
{
    ArgumentNullException.ThrowIfNull(connection);
    _connection = connection;
}
```

- [ ] **Step 2: 添加 EnsureNotDisposed 辅助方法**

在 `CommandTimeout` 属性之后、`Transaction` 属性之前的位置插入：

```csharp
private void EnsureNotDisposed()
{
    if (_disposedValue)
        throw new ObjectDisposedException(nameof(UnitOfWork));
}
```

- [ ] **Step 3: 验证编译**

Run:
```
cd D:\cihong\github\simple-unit-of-work && dotnet build src/SimpleUnitOfWork/SimpleUnitOfWork.csproj
```
Expected: 编译成功

- [ ] **Step 4: 提交**

```bash
git add src/SimpleUnitOfWork/UnitOfWork.cs
git commit -m "fix: add null guard and EnsureNotDisposed helper

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 2: 修复 Connection 属性 getter（去掉 Demand() 副作用）

**Files:**
- Modify: `src/SimpleUnitOfWork/UnitOfWork.cs:19-27`

- [ ] **Step 1: 去掉 getter 中的 Demand() 调用**

当前代码（第 19-27 行）：
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

改为：
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

- [ ] **Step 2: 验证编译**

Run:
```
cd D:\cihong\github\simple-unit-of-work && dotnet build src/SimpleUnitOfWork/SimpleUnitOfWork.csproj
```
Expected: 编译成功

- [ ] **Step 3: 提交**

```bash
git add src/SimpleUnitOfWork/UnitOfWork.cs
git commit -m "fix: remove Demand() side-effect from Connection getter

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 3: 修复 Demand(level) — 使用 _connection 字段 + 自动 Open

**Files:**
- Modify: `src/SimpleUnitOfWork/UnitOfWork.cs:57-63`

- [ ] **Step 1: 改字段引用 + 加自动 Open**

当前代码（第 57-63 行）：
```csharp
public void Demand(IsolationLevel level)
{
    if (_transaction == null)
    {
        _transaction = Connection.BeginTransaction(level);
    }
}
```

改为：
```csharp
public void Demand(IsolationLevel level)
{
    EnsureNotDisposed();
    if (_transaction == null)
    {
        if (_connection.State != ConnectionState.Open)
            _connection.Open();
        _transaction = _connection.BeginTransaction(level);
    }
}
```

- [ ] **Step 2: 验证编译**

Run:
```
cd D:\cihong\github\simple-unit-of-work && dotnet build src/SimpleUnitOfWork/SimpleUnitOfWork.csproj
```
Expected: 编译成功

- [ ] **Step 3: 提交**

```bash
git add src/SimpleUnitOfWork/UnitOfWork.cs
git commit -m "fix: use _connection field in Demand() and auto-open connection

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 4: 修复 Transaction 属性 getter（加 EnsureNotDisposed）

**Files:**
- Modify: `src/SimpleUnitOfWork/UnitOfWork.cs:37-44`

- [ ] **Step 1: Transaction getter 加 disposed 检查**

当前代码（第 37-44 行）：
```csharp
public IDbTransaction Transaction
{
    get
    {
        Demand();
        return _transaction!;
    }
}
```

改为：
```csharp
public IDbTransaction Transaction
{
    get
    {
        EnsureNotDisposed();
        Demand();
        return _transaction!;
    }
}
```

- [ ] **Step 2: 验证编译**

Run:
```
cd D:\cihong\github\simple-unit-of-work && dotnet build src/SimpleUnitOfWork/SimpleUnitOfWork.csproj
```
Expected: 编译成功

- [ ] **Step 3: 提交**

```bash
git add src/SimpleUnitOfWork/UnitOfWork.cs
git commit -m "fix: add disposed guard to Transaction getter

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 5: 修复 Commit() — try-finally + EnsureNotDisposed

**Files:**
- Modify: `src/SimpleUnitOfWork/UnitOfWork.cs:76-84`

- [ ] **Step 1: Commit 加 try-finally 保护**

当前代码（第 76-84 行）：
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

改为：
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

- [ ] **Step 2: 验证编译**

Run:
```
cd D:\cihong\github\simple-unit-of-work && dotnet build src/SimpleUnitOfWork/SimpleUnitOfWork.csproj
```
Expected: 编译成功

- [ ] **Step 3: 提交**

```bash
git add src/SimpleUnitOfWork/UnitOfWork.cs
git commit -m "fix: add try-finally to Commit() to ensure cleanup on failure

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 6: 修复 Rollback() — try-finally + EnsureNotDisposed

**Files:**
- Modify: `src/SimpleUnitOfWork/UnitOfWork.cs:89-97`

- [ ] **Step 1: Rollback 加 try-finally 保护**

当前代码（第 89-97 行）：
```csharp
public void Rollback()
{
    if (_transaction != null)
    {
        _transaction.Rollback();
        _transaction.Dispose();
        _transaction = null;
    }
}
```

改为：
```csharp
public void Rollback()
{
    EnsureNotDisposed();
    if (_transaction != null)
    {
        try
        {
            _transaction.Rollback();
        }
        finally
        {
            _transaction.Dispose();
            _transaction = null;
        }
    }
}
```

- [ ] **Step 2: 验证编译**

Run:
```
cd D:\cihong\github\simple-unit-of-work && dotnet build src/SimpleUnitOfWork/SimpleUnitOfWork.csproj
```
Expected: 编译成功

- [ ] **Step 3: 提交**

```bash
git add src/SimpleUnitOfWork/UnitOfWork.cs
git commit -m "fix: add try-finally to Rollback() to ensure cleanup on failure

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 7: 修复 Dispose(bool) — 用字段 + try-catch

**Files:**
- Modify: `src/SimpleUnitOfWork/UnitOfWork.cs:102-121`

- [ ] **Step 1: Dispose(bool) 重构**

当前代码（第 102-121 行）：
```csharp
protected virtual void Dispose(bool disposing)
{
    if (!_disposedValue)
    {
        if (disposing)
        {
            Rollback();
            if (Connection != null)
            {
                Connection.Dispose();
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
            try
            {
                Rollback();
            }
            catch
            {
                // Dispose 绝不能抛异常 — 吞掉 Rollback 的失败
            }

            _connection?.Dispose();
        }
        _disposedValue = true;
    }
}
```

- [ ] **Step 2: 验证编译**

Run:
```
cd D:\cihong\github\simple-unit-of-work && dotnet build src/SimpleUnitOfWork/SimpleUnitOfWork.csproj
```
Expected: 编译成功

- [ ] **Step 3: 提交**

```bash
git add src/SimpleUnitOfWork/UnitOfWork.cs
git commit -m "fix: prevent exception leak and orphan transaction in Dispose

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 8: Repository 构造函数加 null 检查

**Files:**
- Modify: `src/SimpleUnitOfWork/Repository.cs:16-18`

- [ ] **Step 1: 构造函数加 ArgumentNullException**

当前代码（第 16-18 行）：
```csharp
public Repository(IUnitOfWork unitOfWork)
{
    _unitOfWork = unitOfWork;
}
```

改为：
```csharp
public Repository(IUnitOfWork unitOfWork)
{
    ArgumentNullException.ThrowIfNull(unitOfWork);
    _unitOfWork = unitOfWork;
}
```

- [ ] **Step 2: 验证编译**

Run:
```
cd D:\cihong\github\simple-unit-of-work && dotnet build src/SimpleUnitOfWork/SimpleUnitOfWork.csproj
```
Expected: 编译成功

- [ ] **Step 3: 提交**

```bash
git add src/SimpleUnitOfWork/Repository.cs
git commit -m "fix: add null guard to Repository constructor

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 9: 最终验证 + 确认

- [ ] **Step 1: 完整编译**

Run:
```
cd D:\cihong\github\simple-unit-of-work && dotnet build src/SimpleUnitOfWork/SimpleUnitOfWork.csproj --no-restore
```
Expected: 编译成功，0 warnings

- [ ] **Step 2: Review 最终改动汇总**

Run:
```
cd D:\cihong\github\simple-unit-of-work && git log --oneline -10
```
Expected: 看到 8 个提交，每个对应一个 Task

- [ ] **Step 3: 展示最终代码**

运行 `git diff HEAD~8..HEAD` 确认所有改动正确且无遗漏。
