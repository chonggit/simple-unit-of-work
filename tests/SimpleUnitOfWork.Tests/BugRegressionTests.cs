using System.Data;
using Moq;
using SimpleUnitOfWork;

namespace SimpleUnitOfWork.Tests;

public class BugRegressionTests
{
    /// <summary>
    /// Bug #1: 当 Demand() 内的 BeginTransaction 抛异常后，
    /// _hasUntransactedAccess 标记未被重置，导致后续所有 Demand() 调用失败。
    /// 预期：第二次 Demand() 应该能成功创建事务（标记应在异常路径上也重置，或不在失败路径上置位）。
    /// </summary>
    [Fact]
    public void Demand_ShouldAllowRetry_AfterBeginTransactionFailure()
    {
        // Arrange: connection 是 Open 状态，跳过 EnsureConnectionOpen 的 Open/Broken 逻辑
        var mockConn = new Mock<IDbConnection>();
        mockConn.Setup(c => c.State).Returns(ConnectionState.Open);

        var mockTran = new Mock<IDbTransaction>();

        // 第一次 BeginTransaction 抛异常，第二次成功
        var callCount = 0;
        mockConn.Setup(c => c.BeginTransaction(It.IsAny<IsolationLevel>()))
            .Returns(() =>
            {
                callCount++;
                if (callCount == 1)
                    throw new Exception("Simulated DB failure");
                return mockTran.Object;
            });

        var uow = new UnitOfWork(mockConn.Object, autoTransaction: false);

        // 第一次 Demand 应该抛异常（BeginTransaction 失败）
        Assert.Throws<Exception>(() => uow.Demand(IsolationLevel.ReadCommitted));

        // Bug: 第二次 Demand 应该能成功创建事务
        // 当前代码在 BeginTransaction 失败后 _hasUntransactedAccess 永久为 true，
        // 导致这里抛出 InvalidOperationException("无法开启事务...")
        var ex = Record.Exception(() => uow.Demand(IsolationLevel.ReadCommitted));
        Assert.Null(ex); // 不应该抛异常，应能重试
    }
}
