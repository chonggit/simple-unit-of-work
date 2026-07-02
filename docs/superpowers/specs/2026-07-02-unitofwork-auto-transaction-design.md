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
| `isolationLevel` | `ReadCommitted` | 仅在 `autoTransaction = true` 时生效 |

### 静态工厂方法

```csharp
// 保留（默认行为不变）
public static IUnitOfWork Create()

// 新增重载
public static IUnitOfWork Create(bool autoTransaction)
public static IUnitOfWork Create(bool autoTransaction, IsolationLevel isolationLevel)
```

### IUnitOfWork 接口

**不变。** `Demand()` / `Commit()` / `Rollback()` / `GetRepository<T>()` 签名保持原样。运行时约束在 `UnitOfWork` 实现层完成。

## 内部实现

### UnitOfWorkContext 追踪标记

新增 `_hasUntransactedAccess` 字段：

- `Connection` getter 中，当 `Transaction == null` 时置为 `true`
- 通过 `internal` 属性暴露给 `UnitOfWork`

### Demand() 前置检查

在 `Demand(IsolationLevel)` 开头检查 `_context.HasUntransactedAccess`：
- 若为 `true`：抛出 `InvalidOperationException`，消息指明"无法开启事务：已有操作在无事务状态下执行"
- 若为 `false`：继续原有逻辑

`Demand()` 内部的 `EnsureConnectionOpen()` 会触发 `Connection` getter（置位标记），但检查已在此之前完成，不影响正确性。`BeginTransaction` 成功后显式重置 `_hasUntransactedAccess = false`，保持语义干净。

### Commit/Rollback 错误消息优化

`CompleteTransaction()` 在 `Transaction == null` 时抛异常。区分两种情况：

- 若 `_hasUntransactedAccess == true`（已执行过无事务操作）：`"当前工作单元运行在无事务模式下，无需调用 Commit/Rollback。"`
- 否则：保持原有消息 `"There is no active transaction to complete. Call Demand() first."`

### 构造函数自动开启

`autoTransaction = true` 时，构造完成后立即调用 `Demand(isolationLevel)`。

### Dispose() 安全化

改为仅在 `_context.Transaction != null` 时回滚，无活动事务则静默跳过。避免无事务模式下 `Dispose()` 抛出异常。

## 使用模式

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
var items = repo.GetAll();     // 无事务操作
uow.Demand();                  // ❌ InvalidOperationException
```

## 向后兼容性

- 现有构造函数 `UnitOfWork(IDbConnection)` 调用无需修改，`autoTransaction` 默认 `true`
- 现有 `Create()` 调用无需修改，行为不变
- `IUnitOfWork` 接口不变
- `Repository<T>` 不变
- `UnitOfWorkContext` 新增字段不影响外部（类型为 `internal`）
