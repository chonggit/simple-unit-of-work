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
