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
