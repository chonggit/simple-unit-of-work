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

        private static volatile Action<Exception>? _handleException;

        private readonly object _lock = new();

        /// <summary>
        /// 设置全局异常处理器，用于处理工作单元操作中发生的异常。此方法应在创建任何工作单元实例之前调用。
        /// </summary>
        /// <param name="handler"> 异常处理器 </param>
        public static void SetHandleException(Action<Exception> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            _handleException = handler;
        }

        /// <summary>
        /// 设置用于创建数据库连接的工厂方法。此方法应在创建任何工作单元实例之前调用。
        /// </summary>
        /// <param name="connectionFactory"> 数据库连接工厂方法 </param>
        public static void SetConnectionFactory(Func<IDbConnection> connectionFactory)
        {
            ArgumentNullException.ThrowIfNull(connectionFactory);
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
        /// 当前活动事务。访问之前会确保事务已创建。
        /// </summary>
        public IDbTransaction Transaction
        {
            get
            {
                EnsureNotDisposed();
                Demand();
                return _transaction!;
            }
        }

        /// <summary>
        /// 构造函数，传入可用的数据库连接。
        /// </summary>
        public UnitOfWork(IDbConnection connection)
        {
            ArgumentNullException.ThrowIfNull(connection);
            _connection = connection;
        }

        /// <summary>
        /// 根据指定隔离级别创建事务（如果尚未存在）。
        /// </summary>
        public void Demand(IsolationLevel level)
        {
            EnsureNotDisposed();
            if (_completed)
                throw new InvalidOperationException("This UnitOfWork has already been committed or rolled back.");
            if (_transaction == null)
            {
                if (_connection.State != ConnectionState.Open)
                    _connection.Open();
                _transaction = _connection.BeginTransaction(level);
            }
        }

        /// <summary>
        /// 创建默认隔离级别（ReadCommitted）的事务（如果尚未存在）。
        /// </summary>
        public void Demand()
        {
            Demand(IsolationLevel.ReadCommitted);
        }

        /// <summary>
        /// 提交当前事务并释放事务资源。
        /// </summary>
        public void Commit()
        {
            EnsureNotDisposed();
            if (_transaction != null)
            {
                try
                {
                    _transaction.Commit();
                }
                catch (Exception ex)
                {
                    _handleException?.Invoke(ex);
                    throw;
                }
                finally
                {
                    _transaction.Dispose();
                    _transaction = null;
                    _completed = true;
                }
            }
        }

        /// <summary>
        /// 回滚当前事务并释放事务资源。
        /// </summary>
        public void Rollback()
        {
            EnsureNotDisposed();
            if (_transaction != null)
            {
                try
                {
                    _transaction.Rollback();
                }
                catch (Exception ex)
                {
                    _handleException?.Invoke(ex);
                    throw;
                }
                finally
                {
                    _transaction.Dispose();
                    _transaction = null;
                    _completed = true;
                }
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
                        Rollback();
                    }
                    catch (Exception ex)
                    {
                        _handleException?.Invoke(ex);
                    }

                    _connection?.Dispose();
                }

                // 标记为已释放
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
            // 不要更改此代码。请将清理代码放入“Dispose(bool disposing)”方法中
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
