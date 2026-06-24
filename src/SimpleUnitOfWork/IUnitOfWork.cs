using System.Data;

namespace SimpleUnitOfWork
{
    /// <summary>
    /// 工作单元接口，封装数据库连接、事务及提交/回滚操作。
    /// </summary>
    public interface IUnitOfWork : IDisposable
    {
        /// <summary>
        /// 超时时间，单位秒，默认30秒。
        /// </summary>
        int CommandTimeout { get; set; }

        /// <summary>
        /// 当前活动事务，调用 Demand 后可用。
        /// </summary>
        IDbTransaction Transaction { get; }

        /// <summary>
        /// 当前数据库连接，工作单元负责在 Dispose 时释放。
        /// </summary>
        IDbConnection Connection { get; }

        /// <summary>
        /// 提交当前事务。
        /// </summary>
        void Commit();

        /// <summary>
        /// 回滚当前事务。
        /// </summary>
        void Rollback();

        /// <summary>
        /// 需求一个事务，如果当前没有事务，则创建一个新的事务。
        /// </summary>
        void Demand(IsolationLevel level);
    }
}
