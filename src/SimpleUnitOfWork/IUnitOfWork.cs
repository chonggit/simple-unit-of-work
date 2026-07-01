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

        /// <summary>
        /// 获取与当前工作单元关联的仓储实例。
        /// 返回继承自 Repository&lt;T&gt; 并实现 IRepository&lt;T&gt; 的仓储类型。
        /// </summary>
        /// <typeparam name="TRepository">仓储类型，必须具有接受 IUnitOfWorkContext 的构造函数</typeparam>
        /// <returns>仓储实例</returns>
        /// <exception cref="InvalidOperationException">找不到匹配的构造函数</exception>
        TRepository GetRepository<TRepository>() where TRepository : class;
    }
}
