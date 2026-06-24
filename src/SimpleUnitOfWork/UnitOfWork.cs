using System.Data;

namespace SimpleUnitOfWork
{
    /// <summary>
    /// 工作单元实现，管理数据库连接与事务的生命周期，并提供提交/回滚操作。
    /// </summary>
    public class UnitOfWork : IUnitOfWork
    {
        private volatile bool _disposedValue;

        private volatile bool _completed;

        private IDbTransaction? _transaction;

        private IDbConnection _connection;

        private static volatile Func<IDbConnection>? _connectionFactory;

        private IsolationLevel _isolationLevel = IsolationLevel.ReadCommitted;

        private readonly object _lock = new();

        /// <summary>
        /// 设置用于创建数据库连接的工厂方法。此方法应在创建任何工作单元实例之前调用。
        /// </summary>
        /// <param name="connectionFactory"> 数据库连接工厂方法 </param>
        public static void SetConnectionFactory(Func<IDbConnection> connectionFactory)
        {
            //ArgumentNullException.ThrowIfNull(connectionFactory);
            if (connectionFactory == null)
                throw new ArgumentNullException(nameof(connectionFactory), "Connection factory cannot be null.");
            _connectionFactory = connectionFactory;
        }

        /// <summary>
        /// 创建一个新的工作单元实例，使用预先设置的连接工厂生成数据库连接。
        /// </summary>
        /// <exception cref="InvalidOperationException"></exception>
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
        /// 当前数据库连接，首次访问时确保已初始化事务或直接返回连接。
        /// </summary>
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

        /// <summary>
        /// 命令超时时间（秒），默认 30 秒。
        /// </summary>
        public int CommandTimeout { get; set; } = 30;

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
            if (_connection.State == ConnectionState.Open)
                return;

            if (_connection.State == ConnectionState.Broken)
            {
                try { _connection.Close(); }
                catch { /* Close() 在部分 ADO.NET 实现中对 Broken 连接可能抛出。吞掉后尝试 Open() */ }
            }
            _connection.Open();
        }

        /// <summary>
        /// 当前活动事务。访问之前会确保事务已创建。
        /// </summary>
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

        /// <summary>
        /// 构造函数，传入可用的数据库连接。
        /// </summary>
        public UnitOfWork(IDbConnection connection)
        {
            //ArgumentNullException.ThrowIfNull(connection);
            if (connection == null)
                throw new ArgumentNullException(nameof(connection), "Connection cannot be null.");
            _connection = connection;
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
                if (_transaction != null)
                {
                    if (_isolationLevel != level)
                        throw new InvalidOperationException(
                            $"事务已存在，等级不匹配：当前为 {_isolationLevel}，传入为 {level}。");
                    return;
                }
                _isolationLevel = level;
                EnsureConnectionOpen();
                _transaction = _connection.BeginTransaction(level);
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
            finally
            {
                try { _transaction.Dispose(); }
                catch { /* Dispose 失败不能掩饰 commit/rollback 的异常 */ }
                _transaction = null;
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
        /// 释放资源的受保护实现，按需回滚未提交的事务并释放连接。
        /// </summary>
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

        // // TODO: 仅当“Dispose(bool disposing)”拥有用于释放未托管资源的代码时才替代终结器
        // ~UnitOfWork()
        // {
        //     // 不要更改此代码。请将清理代码放入“Dispose(bool disposing)”方法中
        //     Dispose(disposing: false);
        // }

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
