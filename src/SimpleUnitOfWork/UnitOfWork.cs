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
                try
                {
                    Demand(isolationLevel);
                }
                catch
                {
                    _context.Dispose();
                    throw;
                }
            }
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
                // 检查是否已在无事务状态下执行过操作
                if (_context.HasUntransactedAccess)
                    throw new InvalidOperationException(
                        "无法开启事务：已有操作在无事务状态下执行。请确保 Demand() 在所有数据库操作之前调用。");
                _isolationLevel = level;
                EnsureConnectionOpen();
                try
                {
                    _context.Transaction = _context.Connection.BeginTransaction(level);
                }
                finally
                {
                    // 无论 BeginTransaction 成功或失败，重置标记。
                    // EnsureConnectionOpen 会置位标记，但这是 Demand 内部操作，不算"无事务访问"。
                    _context.HasUntransactedAccess = false;
                }
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
