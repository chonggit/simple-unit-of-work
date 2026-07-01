# IUnitOfWork / Repository<T> 职责分离重构实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将 IUnitOfWork 的连接/事务/超时属性抽取为 IUnitOfWorkContext，Repository<T> 改为依赖 IUnitOfWorkContext，UnitOfWork 新增 GetRepository<T>() 统一管理 Repository 创建

**Architecture:** 新增 IUnitOfWorkContext 接口和 UnitOfWorkContext 内部实现类，精简 IUnitOfWork 接口仅保留行为方法，UnitOfWork 内部持有 UnitOfWorkContext 完成状态委托，Repository<T> 构造函数从 IUnitOfWork 改为 IUnitOfWorkContext

**Tech Stack:** .NET (net461;netstandard2.0;net8.0;net10.0), Dapper.Contrib 2.0.78

## Global Constraints

- 所有公开类型、属性、方法必须包含中文注释
- 多目标框架：net461;netstandard2.0;net8.0;net10.0 不变
- Dapper 2.1.79 / Dapper.Contrib 2.0.78 依赖不变
- IRepository<T> 接口完全不变
- 所有现有 CRUD 行为不变
- UnitOfWork 的静态工厂方法（SetConnectionFactory / Create）保留
- 线程安全机制（lock / volatile）保留
- 完成守卫：UnitOfWorkContext 通过 Func<bool> 回调感知 UnitOfWork._completed

---

### Task 1: 创建 IUnitOfWorkContext 接口

**Files:**
- Create: `src/SimpleUnitOfWork/IUnitOfWorkContext.cs`

**Interfaces:**
- Produces: `IUnitOfWorkContext`（public interface）

- [ ] **Step 1: 创建 IUnitOfWorkContext.cs**

```csharp
using System.Data;

namespace SimpleUnitOfWork
{
    /// <summary>
    /// 工作单元上下文接口，提供数据库连接、事务和超时配置的只读访问。
    /// IUnitOfWorkContext 为 public 接口，允许外部单元测试时 mock 或自行实现。
    /// 但正常情况下不鼓励直接使用——通过 UnitOfWork.GetRepository&lt;T&gt;() 间接访问。
    /// UnitOfWorkContext 实现类标记为 internal，Transaction setter 为 internal。
    /// </summary>
    public interface IUnitOfWorkContext : IDisposable
    {
        /// <summary>
        /// 当前数据库连接，工作单元负责在 Dispose 时释放。
        /// </summary>
        IDbConnection Connection { get; }

        /// <summary>
        /// 当前活动事务，调用 UnitOfWork.Demand 后可用。完成守卫生效期间为 null。
        /// </summary>
        IDbTransaction? Transaction { get; }

        /// <summary>
        /// 超时时间，单位秒，默认 30 秒。
        /// </summary>
        int CommandTimeout { get; }
    }
}
```

- [ ] **Step 2: Commit**

```bash
git add src/SimpleUnitOfWork/IUnitOfWorkContext.cs
git commit -m "feat: 新增 IUnitOfWorkContext 接口，抽取连接/事务/超时上下文职责"
```

---

### Task 2: 创建 UnitOfWorkContext 内部实现类

**Files:**
- Create: `src/SimpleUnitOfWork/UnitOfWorkContext.cs`

**Interfaces:**
- Consumes: `IUnitOfWorkContext`
- Produces: `UnitOfWorkContext`（internal class）

- [ ] **Step 1: 创建 UnitOfWorkContext.cs**

```csharp
using System.Data;

namespace SimpleUnitOfWork
{
    /// <summary>
    /// 工作单元内部上下文实现，管理数据库连接与事务的生命周期。
    /// 此类型为 internal，外部代码通过 UnitOfWork.GetRepository&lt;T&gt;() 间接访问。
    /// 构造函数接收 Func&lt;bool&gt; isCompleted 回调以感知 UnitOfWork 的完成状态。
    /// </summary>
    internal class UnitOfWorkContext : IUnitOfWorkContext
    {
        private readonly IDbConnection _connection;

        private readonly Func<bool> _isCompleted;

        private volatile bool _disposed;

        /// <summary>
        /// 当前数据库连接。完成守卫生效后访问抛出 InvalidOperationException。
        /// </summary>
        public IDbConnection Connection
        {
            get
            {
                if (_isCompleted())
                    throw new InvalidOperationException("This UnitOfWork has already been committed or rolled back.");
                return _connection;
            }
        }

        /// <summary>
        /// 当前活动事务，可能为 null（尚未调用 Demand 或已完成）。完成守卫生效后访问抛出 InvalidOperationException。
        /// Transaction setter 为 internal，仅由 UnitOfWork.Demand() 在锁内写入。
        /// </summary>
        public IDbTransaction? Transaction { get; internal set; }

        /// <summary>
        /// 命令超时时间（秒），默认 30 秒。
        /// </summary>
        public int CommandTimeout { get; set; } = 30;

        /// <summary>
        /// 创建上下文实例，传入数据库连接和完成状态回调。
        /// </summary>
        /// <param name="connection">数据库连接，不能为 null</param>
        /// <param name="isCompleted">完成状态回调函数，指向 UnitOfWork._completed</param>
        /// <exception cref="ArgumentNullException"></exception>
        internal UnitOfWorkContext(IDbConnection connection, Func<bool> isCompleted)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection), "Connection cannot be null.");
            _connection = connection;
            _isCompleted = isCompleted ?? throw new ArgumentNullException(nameof(isCompleted));
        }

        /// <summary>
        /// 释放资源：先释放事务，再释放连接。
        /// </summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                try { Transaction?.Dispose(); }
                catch { /* Dispose 失败不抛异常 */ }
                Transaction = null;

                _connection.Dispose();
                _disposed = true;
            }
        }
    }
}
```

- [ ] **Step 2: Commit**

```bash
git add src/SimpleUnitOfWork/UnitOfWorkContext.cs
git commit -m "feat: 新增 UnitOfWorkContext 内部实现类，含完成守卫和资源管理"
```

---

### Task 3: 精简 IUnitOfWork 接口

**Files:**
- Modify: `src/SimpleUnitOfWork/IUnitOfWork.cs`

**Interfaces:**
- Consumes: 无
- Produces: 精简版 `IUnitOfWork`（仅 Commit / Rollback / Demand / Dispose）

- [ ] **Step 1: 修改 IUnitOfWork.cs**

移除 `CommandTimeout`、`Transaction`、`Connection` 三个属性，只保留行为方法：

```csharp
using System.Data;

namespace SimpleUnitOfWork
{
    /// <summary>
    /// 工作单元接口，管理事务的提交、回滚与创建。
    /// 连接、事务、超时等状态属性请参见 <see cref="IUnitOfWorkContext"/>。
    /// </summary>
    public interface IUnitOfWork : IDisposable
    {
        /// <summary>
        /// 提交当前事务。
        /// </summary>
        void Commit();

        /// <summary>
        /// 回滚当前事务。
        /// </summary>
        void Rollback();

        /// <summary>
        /// 需求一个事务，如果当前没有事务，则根据指定隔离级别创建一个新的事务。
        /// </summary>
        /// <param name="level">事务隔离级别</param>
        void Demand(IsolationLevel level);
    }
}
```

- [ ] **Step 2: Commit**

```bash
git add src/SimpleUnitOfWork/IUnitOfWork.cs
git commit -m "refactor: 精简 IUnitOfWork 接口，移除状态属性（已抽取至 IUnitOfWorkContext）"
```

---

### Task 4: 重构 UnitOfWork 实现类

**Files:**
- Modify: `src/SimpleUnitOfWork/UnitOfWork.cs`

**Interfaces:**
- Consumes: `IUnitOfWork`（精简版）、`UnitOfWorkContext`（internal）、`IUnitOfWorkContext`
- Produces: 重构后的 `UnitOfWork`（内部持有 `UnitOfWorkContext`，新增 `GetRepository<T>()`）

- [ ] **Step 1: 重写 UnitOfWork.cs**

完整的重写内容如下。关键变更：
1. 内部持有 `UnitOfWorkContext _context`
2. 委托 `Connection`/`Transaction`/`CommandTimeout` 给 `_context`
3. `Demand()` 写入 `_context.Transaction`（通过 internal set）
4. `Dispose()` → Rollback（吞异常）→ `_context.Dispose()`
5. 新增 `GetRepository<T>()` 反射创建 Repository
6. 保留 `internal IUnitOfWorkContext Context` 属性供 `GetRepository` 及向下兼容
7. 保留原有线程安全机制（lock + volatile）
8. 保留静态工厂（SetConnectionFactory / Create）
9. 保留 `_handleException`、`CompleteTransaction`、`EnsureConnectionOpen`

```csharp
using System.Data;
using System.Reflection;

namespace SimpleUnitOfWork
{
    /// <summary>
    /// 工作单元实现，管理数据库连接与事务的生命周期，并提供提交/回滚操作。
    /// 内部使用 <see cref="UnitOfWorkContext"/> 管理连接、事务和超时状态。
    /// </summary>
    public class UnitOfWork : IUnitOfWork
    {
        private volatile bool _disposedValue;

        private volatile bool _completed;

        private readonly UnitOfWorkContext _context;

        private static volatile Func<IDbConnection>? _connectionFactory;

        private IsolationLevel _isolationLevel = IsolationLevel.ReadCommitted;

        private readonly object _lock = new();

        /// <summary>
        /// 内部上下文，供 GetRepository&lt;T&gt; 使用，也可用于向下兼容场景。
        /// </summary>
        internal IUnitOfWorkContext Context => _context;

        /// <summary>
        /// 设置用于创建数据库连接的工厂方法。此方法应在创建任何工作单元实例之前调用。
        /// </summary>
        /// <param name="connectionFactory">数据库连接工厂方法</param>
        public static void SetConnectionFactory(Func<IDbConnection> connectionFactory)
        {
            if (connectionFactory == null)
                throw new ArgumentNullException(nameof(connectionFactory), "Connection factory cannot be null.");
            _connectionFactory = connectionFactory;
        }

        /// <summary>
        /// 创建一个新的工作单元实例，使用预先设置的连接工厂生成数据库连接。
        /// </summary>
        /// <exception cref="InvalidOperationException">连接工厂未设置或返回 null</exception>
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

        /// <summary>
        /// 构造函数，传入可用的数据库连接。
        /// </summary>
        /// <param name="connection">数据库连接，不能为 null</param>
        public UnitOfWork(IDbConnection connection)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection), "Connection cannot be null.");
            _context = new UnitOfWorkContext(connection, () => _completed);
        }

        /// <summary>
        /// 检查当前实例是否已释放，若已释放则抛出 <see cref="ObjectDisposedException"/>。
        /// </summary>
        private void EnsureNotDisposed()
        {
            if (_disposedValue)
                throw new ObjectDisposedException(nameof(UnitOfWork));
        }

        /// <summary>
        /// 确保连接处于 Open 状态。处理 ConnectionState.Broken 等异常状态。
        /// </summary>
        private void EnsureConnectionOpen()
        {
            var connection = _context.Connection; // 通过 Context 获取（含完成守卫）
            if (connection.State == ConnectionState.Open)
                return;

            if (connection.State == ConnectionState.Broken)
            {
                try { connection.Close(); }
                catch { /* Close() 在部分 ADO.NET 实现中对 Broken 连接可能抛出 */ }
            }
            connection.Open();
        }

        /// <summary>
        /// 根据指定隔离级别创建事务（如果尚未存在）。
        /// 如果事务已存在但隔离级别与当前记录不一致，则抛出异常。
        /// </summary>
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

        /// <summary>
        /// 确保事务已存在。如果尚无事务，使用当前记录的默认隔离级别创建。
        /// </summary>
        public void Demand()
        {
            Demand(_isolationLevel);
        }

        /// <summary>
        /// 执行事务提交/回滚操作的公共骨架。处理 _completed 守卫、异常处理、资源清理。
        /// </summary>
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

        /// <summary>
        /// 提交当前事务并释放事务资源。
        /// </summary>
        public void Commit()
        {
            lock (_lock)
            {
                CompleteTransaction(t => t.Commit());
            }
        }

        /// <summary>
        /// 回滚当前事务并释放事务资源。
        /// </summary>
        public void Rollback()
        {
            lock (_lock)
            {
                CompleteTransaction(t => t.Rollback());
            }
        }

        /// <summary>
        /// 获取与当前工作单元关联的仓储实例。
        /// 返回继承自 Repository&lt;T&gt; 并实现 IRepository&lt;T&gt; 的仓储类型。
        /// </summary>
        /// <typeparam name="TRepository">仓储类型，必须具有接受 IUnitOfWorkContext 的构造函数</typeparam>
        /// <returns>仓储实例</returns>
        /// <exception cref="InvalidOperationException">找不到匹配的构造函数</exception>
        public TRepository GetRepository<TRepository>() where TRepository : class
        {
            var ctor = typeof(TRepository).GetConstructor(new[] { typeof(IUnitOfWorkContext) });
            if (ctor == null)
                throw new InvalidOperationException(
                    $"{typeof(TRepository).Name} must have a constructor that accepts IUnitOfWorkContext.");
            return (TRepository)ctor.Invoke(new object[] { Context });
        }

        /// <summary>
        /// 释放资源的受保护实现，按需回滚未提交的事务并释放上下文。
        /// </summary>
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

        /// <summary>
        /// 释放所有资源，调用受保护的 Dispose 实现并禁止终结器运行。
        /// </summary>
        public void Dispose()
        {
            lock (_lock)
            {
                Dispose(disposing: true);
                GC.SuppressFinalize(this);
            }
        }
    }
}
```

- [ ] **Step 2: 验证编译**

```bash
dotnet build src/SimpleUnitOfWork/SimpleUnitOfWork.csproj
```
Expected: 编译成功（所有 `_context` 委托正确，`_context.Connection.BeginTransaction` 合法）

- [ ] **Step 3: Commit**

```bash
git add src/SimpleUnitOfWork/UnitOfWork.cs
git commit -m "refactor: 重构 UnitOfWork，内部持有 UnitOfWorkContext，新增 GetRepository<T>"
```

---

### Task 5: 修改 Repository<T> 构造函数依赖

**Files:**
- Modify: `src/SimpleUnitOfWork/Repository.cs`

**Interfaces:**
- Consumes: `IUnitOfWorkContext`
- Modifies: `Repository<T>`（构造函数参数变更）

- [ ] **Step 1: 修改 Repository.cs**

将构造函数参数从 `IUnitOfWork` 改为 `IUnitOfWorkContext`，对应更新属性访问：

```csharp
using Dapper.Contrib.Extensions;
using System.Data;

namespace SimpleUnitOfWork
{
    /// <summary>
    /// 通用仓储实现，基于 Dapper.Contrib 提供增删改查操作。
    /// </summary>
    public class Repository<T> : IRepository<T> where T : class, new()
    {
        private readonly IUnitOfWorkContext _context;

        /// <summary>
        /// 构造函数，注入工作单元上下文以使用共享连接和事务。
        /// </summary>
        /// <param name="context">工作单元上下文</param>
        public Repository(IUnitOfWorkContext context)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context), "工作单元上下文不能为空。");
            _context = context;
        }

        /// <summary>
        /// 当前数据库连接，来自注入的工作单元上下文。
        /// </summary>
        protected virtual IDbConnection Connection => _context.Connection;

        /// <summary>
        /// 当前事务，来自注入的工作单元上下文。
        /// </summary>
        protected virtual IDbTransaction Transaction => _context.Transaction;

        /// <summary>
        /// 命令超时时间（秒），来自注入的工作单元上下文。
        /// </summary>
        protected virtual int CommandTimeout => _context.CommandTimeout;

        // ---- CRUD 方法保持不变 ----

        /// <summary>
        /// 同步添加实体并返回新记录的主键。
        /// </summary>
        public virtual long Add(T entity) => Connection.Insert(entity, Transaction, CommandTimeout);

        /// <summary>
        /// 异步添加实体并返回新记录的主键。
        /// </summary>
        public virtual async Task<long> AddAsync(T entity) => await Connection.InsertAsync(entity, Transaction, CommandTimeout);

        /// <summary>
        /// 同步更新实体，返回是否成功。
        /// </summary>
        public virtual bool Update(T entity) => Connection.Update(entity, Transaction, CommandTimeout);

        /// <summary>
        /// 异步更新实体，返回是否成功。
        /// </summary>
        public virtual async Task<bool> UpdateAsync(T entity) => await Connection.UpdateAsync(entity, Transaction, CommandTimeout);

        /// <summary>
        /// 同步删除实体，返回是否成功。
        /// </summary>
        public virtual bool Delete(T entity) => Connection.Delete(entity, Transaction, CommandTimeout);

        /// <summary>
        /// 异步删除实体，返回是否成功。
        /// </summary>
        public virtual async Task<bool> DeleteAsync(T entity) => await Connection.DeleteAsync(entity, Transaction, CommandTimeout);

        /// <summary>
        /// 同步根据主键获取实体。
        /// </summary>
        public virtual T GetById(object id) => Connection.Get<T>(id, Transaction, CommandTimeout);

        /// <summary>
        /// 异步根据主键获取实体。
        /// </summary>
        public virtual async Task<T> GetByIdAsync(object id) => await Connection.GetAsync<T>(id, Transaction, CommandTimeout);

        /// <summary>
        /// 同步获取所有实体集合。
        /// </summary>
        public virtual IEnumerable<T> GetAll() => Connection.GetAll<T>(Transaction, CommandTimeout);

        /// <summary>
        /// 异步获取所有实体集合。
        /// </summary>
        public virtual async Task<IEnumerable<T>> GetAllAsync() => await Connection.GetAllAsync<T>(Transaction, CommandTimeout);
    }
}
```

- [ ] **Step 2: 验证编译**

```bash
dotnet build src/SimpleUnitOfWork/SimpleUnitOfWork.csproj
```
Expected: 编译成功

- [ ] **Step 3: Commit**

```bash
git add src/SimpleUnitOfWork/Repository.cs
git commit -m "refactor: Repository<T> 构造函数改为依赖 IUnitOfWorkContext"
```

---

### Task 6: 终验证 — 全量编译

- [ ] **Step 1: 全量编译确认**

```bash
dotnet build src/SimpleUnitOfWork/SimpleUnitOfWork.csproj
```
Expected: Build succeeded, 0 warnings, 0 errors

- [ ] **Step 2: 最终提交**

```bash
git add -A
git commit -m "feat: 完成 IUnitOfWork/Repository<T> 职责分离重构"
```
