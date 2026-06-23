using System.Data;

namespace SimpleUnitOfWork
{
    /// <summary>
    /// 工作单元实现，管理数据库连接与事务的生命周期，并提供提交/回滚操作。
    /// </summary>
    public class UnitOfWork : IUnitOfWork
    {
        private bool _disposedValue;

        private IDbTransaction? _transaction;

        private IDbConnection _connection;

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
            if (_transaction == null)
            {
                _transaction = Connection.BeginTransaction(level);
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
            if (_transaction != null)
            {
                _transaction.Commit();
                _transaction.Dispose();
                _transaction = null;
            }
        }

        /// <summary>
        /// 回滚当前事务并释放事务资源。
        /// </summary>
        public void Rollback()
        {
            if (_transaction != null)
            {
                _transaction.Rollback();
                _transaction.Dispose();
                _transaction = null;
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

                    // 释放托管资源：回滚未提交事务并释放连接
                    Rollback();

                    if (Connection != null)
                    {
                        Connection.Dispose();
                    }
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
