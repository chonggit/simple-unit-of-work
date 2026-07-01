using System.Data;

namespace SimpleUnitOfWork
{
    /// <summary>
    /// 工作单元上下文接口，提供数据库连接、事务和超时配置的只读访问。
    /// IUnitOfWorkContext 为 public 接口，允许外部单元测试时 mock 或自行实现。
    /// 但正常情况下不鼓励直接使用——通过 UnitOfWork.GetRepository&lt;T&gt;() 间接访问。
    /// UnitOfWorkContext 实现类标记为 internal，Transaction setter 为 internal。
    /// </summary>
    public interface IUnitOfWorkContext : IDisposable
    {
        /// <summary>
        /// 当前数据库连接，工作单元负责在 Dispose 时释放。
        /// </summary>
        IDbConnection Connection { get; }

        /// <summary>
        /// 当前活动事务，调用 UnitOfWork.Demand 后可用。完成守卫生效期间为 null。
        /// </summary>
        IDbTransaction? Transaction { get; }

        /// <summary>
        /// 超时时间，单位秒，默认 30 秒。
        /// </summary>
        int CommandTimeout { get; }
    }
}
