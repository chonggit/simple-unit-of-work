# UnitOfWork 自动开启事务参数 — 设计文档

日期：2026-07-02

## 目标

为 `UnitOfWork` 构造函数增加 `autoTransaction` 参数，允许使用者选择：
- **自动开启事务（默认）**：行为与当前一致
- **手动开启或完全不开启事务**：一个 UnitOfWork 要么完全在事务中运行，要么完全不用事务，不允许中途切换

## API 变更

### 构造函数

```csharp
public UnitOfWork(
    IDbConnection connection,
    bool autoTransaction = true,
    IsolationLevel isolationLevel = IsolationLevel.ReadCommitted)
```

| 参数 | 默认值 | 说明 |
|---|---|---|
| `connection` | (必填) | 数据库连接 |
| `autoTransaction` | `true` | 构造时自动调用 `Demand(isolationLevel)` |
| `isolationLevel` | `ReadCommitted` | 始终存入 `_isolationLevel` 字段。`autoTransaction = true` 时自动用于 `Demand()`；`autoTransaction = false` 时被后续无参 `Demand()` 调用使用 |

### 静态工厂方法

```csharp
// 保留（默认行为不变）
public static IUnitOfWork Create()

// 新增重载
public static IUnitOfWork Create(bool autoTransaction)
public static IUnitOfWork Create(bool autoTransaction, IsolationLevel isolationLevel)
```

**连接泄漏防护**：所有 `Create()` 重载内部需用 try-catch 包裹构造函数调用。若构造函数失败（如 `autoTransaction = true` 时 `Demand()` 抛异常），需在 catch 中释放已创建的 `IDbConnection`，再重新抛出原始异常。建议将连接创建与 UnitOfWork 构造提取为一个 private helper 方法以消除三个重载间的重复代码，具体重构方式由实施者决定。

### IUnitOfWork 接口

**不变。** `Demand()` / `Commit()` / `Rollback()` / `GetRepository<T>()` 签名保持原样。运行时约束在 `UnitOfWork` 实现层完成。

## 内部实现

### UnitOfWorkContext 追踪标记

新增 `_hasUntransactedAccess` 字段（`volatile`）：

- `Connection` getter 中，当 `Transaction == null` 时置为 `true`（写入不在锁内）
- `Demand()` 在 `lock(_lock)` 内读取，`volatile` 确保与锁的内存屏障配合，防止读到陈旧值
- 通过 `internal` 属性暴露给 `UnitOfWork`

### Demand() 前置检查

在 `Demand(IsolationLevel)` 开头（`lock` 内部）检查 `_context.HasUntransactedAccess`：
- 若为 `true`：抛出 `InvalidOperationException`，消息："无法开启事务：已有操作在无事务状态下执行。请确保 Demand() 在所有数据库操作之前调用。"
- 若为 `false`：继续原有逻辑

`Demand()` 内部的 `EnsureConnectionOpen()` 会触发 `Connection` getter（置位标记），但检查已在此之前完成，不影响正确性。`BeginTransaction` 成功后显式重置 `_hasUntransactedAccess = false`，保持语义干净。

### Commit/Rollback 错误消息优化

`CompleteTransaction()` 在 `Transaction == null` 时抛异常。区分两种情况：

- 若 `_hasUntransactedAccess == true`（已执行过无事务操作）：`"当前工作单元运行在无事务模式下，无需调用 Commit/Rollback。"`
- 否则：保持原有消息 `"There is no active transaction to complete. Call Demand() first."`

### 构造函数自动开启

`autoTransaction = true` 时，构造完成后立即调用 `Demand(isolationLevel)`。

`isolationLevel` 参数始终存入 `_isolationLevel` 字段：
- `autoTransaction = true` → 自动 `Demand(isolationLevel)` 使用
- `autoTransaction = false` → 后续手动无参 `Demand()` 使用该值

### Dispose() 安全化

条件判断**保留在 `Dispose(bool)` 内部**，不改变现有锁结构：

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
                try { Rollback(); }
                catch { /* 吞掉 */ }
            }
            _context.Dispose();
        }
        _disposedValue = true;
    }
}
```

`Dispose()` 持有 `lock(_lock)` 调用 `Dispose(bool)` → `Rollback()` → `CompleteTransaction()` → 再次获取 `lock(_lock)`。依赖 C# `lock` 的重入特性，此行为不变，不引入新的无锁路径。

## 使用模式

**调用顺序约束**：当 `autoTransaction = false` 时，若打算使用事务，`Demand()` 必须在所有数据库操作（包括 `GetRepository<T>()` 之后的 CRUD 调用）之前调用。`GetRepository<T>()` 本身不触发 `_hasUntransactedAccess`，但获取 Repository 后立即调用 CRUD 方法会触发标记，导致后续 `Demand()` 失败。

### 模式 1：自动事务（默认，与现在一致）

```csharp
using var uow = UnitOfWork.Create();
var repo = uow.GetRepository<MyRepository>();
repo.Add(entity);
uow.Commit();
```

### 模式 2：手动开启事务

```csharp
using var uow = UnitOfWork.Create(autoTransaction: false);
uow.Demand(IsolationLevel.Serializable); // 必须在所有操作前调用
var repo = uow.GetRepository<MyRepository>();
repo.Add(entity);
uow.Commit();
```

### 模式 3：完全无事务

```csharp
using var uow = UnitOfWork.Create(autoTransaction: false);
var repo = uow.GetRepository<MyRepository>();
var items = repo.GetAll(); // 无事务，Dispose 时静默释放连接
```

### 非法模式（运行时被拒绝）

```csharp
using var uow = UnitOfWork.Create(autoTransaction: false);
var repo = uow.GetRepository<MyRepository>();
var items = repo.GetAll();     // 无事务操作 → 置位 _hasUntransactedAccess
uow.Demand();                  // ❌ InvalidOperationException
```

## 向后兼容性

- 现有构造函数 `UnitOfWork(IDbConnection)` 调用无需修改，`autoTransaction` 默认 `true`
- 现有 `Create()` 调用无需修改，行为不变
- `IUnitOfWork` 接口不变
- `Repository<T>` 不变
- `UnitOfWorkContext` 新增字段不影响外部（类型为 `internal`）
