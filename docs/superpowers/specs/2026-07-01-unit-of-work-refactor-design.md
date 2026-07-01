# IUnitOfWork / Repository<T> 职责分离重构设计

> 重构 IUnitOfWork，将状态属性抽取为 IUnitOfWorkContext，Repository<T> 改为依赖 IUnitOfWorkContext

## 动机

当前 `IUnitOfWork` 接口承担过多职责：既管理事务行为（Commit/Rollback/Demand），又暴露连接状态属性（Connection/Transaction/CommandTimeout）。`Repository<T>` 直接依赖 `IUnitOfWork` 获取连接和事务，导致其与工作单元的完整生命周期耦合。

重构目标：
- 分离"行为"与"状态"职责
- `Repository<T>` 只需读取连接/事务，不应参与提交/回滚决策
- 统一 Repository 的获取入口

## 架构

```
IUnitOfWork (行为)             IUnitOfWorkContext (状态)        IRepository<T> (CRUD)
┌──────────────────────┐      ┌────────────────────────┐      ┌──────────────────────┐
│ Commit()             │      │ IDbConnection Connection│      │ Add / Update         │
│ Rollback()           │      │ IDbTransaction? Trans.  │      │ Delete / GetById     │
│ Demand(IsolationLv)  │      │ int CommandTimeout      │      │ GetAll               │
│ GetRepository<T>()   │      │ Dispose()               │      │ (sync + async)       │
│ Dispose()            │      └────────────────────────┘      └──────────────────────┘
└──────────────────────┘                 ▲                              ▲
         ▲                               │                              │
         │                        UnitOfWorkContext              Repository<T>
    UnitOfWork                     (internal)                   ctor(IUnitOfWorkContext)
    ┌─────────────────────┐
    │ - static factory    │
    │ - thread safety     │
    │ - owns Context      │
    └─────────────────────┘
```

## 接口定义

### IUnitOfWorkContext（新增）

```csharp
public interface IUnitOfWorkContext : IDisposable
{
    /// <summary>当前数据库连接</summary>
    IDbConnection Connection { get; }

    /// <summary>当前活动事务，可能为 null</summary>
    IDbTransaction? Transaction { get; }

    /// <summary>命令超时时间（秒），默认 30</summary>
    int CommandTimeout { get; }
}
```

### IUnitOfWork（精简）

```csharp
public interface IUnitOfWork : IDisposable
{
    void Commit();
    void Rollback();
    void Demand(IsolationLevel level);
}
```

### IRepository<T>（不变）

```csharp
public interface IRepository<T> where T : class, new()
{
    long Add(T entity);
    Task<long> AddAsync(T entity);
    bool Delete(T entity);
    Task<bool> DeleteAsync(T entity);
    IEnumerable<T> GetAll();
    Task<IEnumerable<T>> GetAllAsync();
    T GetById(object id);
    Task<T> GetByIdAsync(object id);
    bool Update(T entity);
    Task<bool> UpdateAsync(T entity);
}
```

## 实现类设计

### UnitOfWorkContext

| 成员 | 说明 |
|------|------|
| `Connection` | 构造时注入，只读。通过 `_isCompleted` 回调守卫 |
| `Transaction` | `internal set`，仅由 `UnitOfWork.Demand()` 在锁内写入 |
| `CommandTimeout` | 默认 30，可读写 |
| `Dispose()` | `_disposed` 守卫，释放 Transaction + Connection |
| `_isCompleted` | `Func<bool>` 回调，指向 `UnitOfWork._completed`，`volatile` 保证可见性 |

### UnitOfWork

| 成员 | 说明 |
|------|------|
| `_context` | `UnitOfWorkContext` 实例，生命周期由 `UnitOfWork` 管理 |
| `Demand(level)` | 委托给原实现，写入 `_context.Transaction`（通过 `internal set`） |
| `Commit()` | 原 `CompleteTransaction` 逻辑，设置 `_completed = true` |
| `Rollback()` | 同 Commit，回滚后设置 `_completed = true` |
| `Dispose()` | `lock` → Rollback（吞异常）→ `_context.Dispose()` |
| `GetRepository<T>()` | 反射构造 `TRepository(IUnitOfWorkContext)` |
| `SetConnectionFactory()` | 静态，`volatile` 字段 |
| `Create()` | 静态，调用工厂创建连接并返回 `new UnitOfWork(connection)` |

**锁分布：** `Demand`, `Commit`, `Rollback`, `Dispose` 由 `lock` 保护，_completed 为 `volatile`。

### Repository<T>

改动：构造函数参数从 `IUnitOfWork` 改为 `IUnitOfWorkContext`。

```csharp
public Repository(IUnitOfWorkContext context)
{
    _context = context ?? throw new ArgumentNullException(nameof(context));
}

protected virtual IDbConnection Connection => _context.Connection;
protected virtual IDbTransaction Transaction => _context.Transaction;
protected virtual int CommandTimeout => _context.CommandTimeout;
```

CRUD 方法体不变，仍调用 Dapper.Contrib 方法。

## 完成守卫

`UnitOfWorkContext` 通过构造函数接收 `Func<bool> isCompleted` 回调：

```csharp
internal UnitOfWorkContext(IDbConnection connection, Func<bool> isCompleted)
{
    Connection = connection;
    _isCompleted = isCompleted;
}
```

在 `Connection` 和 `Transaction` getter 中守卫：

```csharp
get
{
    if (_isCompleted())
        throw new InvalidOperationException("UnitOfWork has already been committed or rolled back.");
    return _connection;  // or _transaction
}
```

这确保 Commit/Rollback 后通过 Repository 进行的任何操作都会立即抛异常，防止出现无事务的隐式提交。

## 异常处理

沿用当前 `CompleteTransaction` + `_handleException` 机制，异常路径不变：

| 场景 | 行为 |
|------|------|
| Demand 连接失败 | `InvalidOperationException` 冒泡 |
| Commit/Rollback action 失败 | `_transaction` 已 Dispose，`_completed` 保持 false，原始异常冒泡 |
| Dispose 时 Rollback 失败 | 吞掉，继续 Dispose `_context` |
| Dispose 时 `_context.Dispose()` 失败 | 吞掉（`_disposed` 内部保护） |
| `_completed` 后访问 Context | `InvalidOperationException` |
| `_completed` 后调 Commit/Rollback | `InvalidOperationException` |

## 生命周期

```
using var uow = UnitOfWork.Create();
var repo = uow.GetRepository<CustomerRepository>();

uow.Demand(IsolationLevel.ReadCommitted);
repo.Add(entity);
uow.Commit();
// uow.Dispose() → Rollback 无操作（已提交）→ _context.Dispose()
```

```
using var uow = UnitOfWork.Create();
var repo = uow.GetRepository<CustomerRepository>();

uow.Demand(IsolationLevel.ReadCommitted);
try { repo.Add(entity); uow.Commit(); }
catch
{
    uow.Rollback();   // _completed = true
    throw;
}
// uow.Dispose() → Rollback 无操作（已 completed）→ _context.Dispose()
```

## 调用方迁移

重构后调用方需要适配的点：

| 现状 | 重构后 |
|------|--------|
| `repo = new Repository<T>(uow)` | `repo = uow.GetRepository<TRepository>()` |
| `uow.Connection` | 移除，通过 `IUnitOfWorkContext` 获取 |
| `uow.Transaction` | 移除，通过 `IUnitOfWorkContext` 获取 |
| `uow.CommandTimeout` | 移除，通过 `IUnitOfWorkContext` 获取 |
| 静态工厂 | 不变 |

## 排除项（YAGNI）

- `GetRepository<T>()` 不加入缓存/池化（每次调用反射创建新实例）
- `IRepository<T>` 不新增方法（如分页、条件查询）
- 不引入 DI 容器集成
- 不修改 `IUnitOfWorkContext` 为可写公共接口（Transaction 仅通过 `internal set` 写入）
