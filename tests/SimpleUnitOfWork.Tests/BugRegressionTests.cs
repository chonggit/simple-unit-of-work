using System.Data;
using Moq;
using SimpleUnitOfWork;

namespace SimpleUnitOfWork.Tests;

public class BugRegressionTests
{
    /// <summary>
    /// Bug #1: 当 Demand() 内的 BeginTransaction 抛异常后，
    /// _hasUntransactedAccess 标记未被重置，导致后续所有 Demand() 调用失败。
    /// 预期：第二次 Demand() 应该能成功创建事务。
    /// </summary>
    [Fact]
    public void Demand_ShouldAllowRetry_AfterBeginTransactionFailure()
    {
        var mockConn = new Mock<IDbConnection>();
        mockConn.Setup(c => c.State).Returns(ConnectionState.Open);

        var mockTran = new Mock<IDbTransaction>();

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

        Assert.Throws<Exception>(() => uow.Demand(IsolationLevel.ReadCommitted));

        var ex = Record.Exception(() => uow.Demand(IsolationLevel.ReadCommitted));
        Assert.Null(ex);
    }

    /// <summary>
    /// Bug #2: 构造函数内 Demand() 失败后，_context 未被释放。
    /// </summary>
    [Fact]
    public void Constructor_ShouldDisposeConnection_WhenAutoTransactionFails()
    {
        var mockConn = new Mock<IDbConnection>();
        mockConn.Setup(c => c.State).Returns(ConnectionState.Open);
        mockConn.Setup(c => c.BeginTransaction(It.IsAny<IsolationLevel>()))
            .Throws(new Exception("Simulated DB failure"));

        try { _ = new UnitOfWork(mockConn.Object, autoTransaction: true); }
        catch (Exception) { /* 预期抛出 */ }

        mockConn.Verify(c => c.Dispose(), Times.AtLeastOnce);
    }

    /// <summary>
    /// Bug #3: GetRepository 缺少 Dispose 守卫。
    /// </summary>
    [Fact]
    public void GetRepository_ShouldThrowObjectDisposedException_AfterDispose()
    {
        var mockConn = new Mock<IDbConnection>();
        mockConn.Setup(c => c.State).Returns(ConnectionState.Open);
        var uow = new UnitOfWork(mockConn.Object, autoTransaction: false);
        uow.Dispose();

        Assert.Throws<ObjectDisposedException>(() => uow.GetRepository<FakeRepository>());
    }

    /// <summary>
    /// Bug #4: Connection getter 缺 _disposed 守卫。
    /// </summary>
    [Fact]
    public void ContextConnection_ShouldThrow_AfterDisposeWithoutTransaction()
    {
        var mockConn = new Mock<IDbConnection>();
        mockConn.Setup(c => c.State).Returns(ConnectionState.Open);
        var uow = new UnitOfWork(mockConn.Object, autoTransaction: false);
        uow.Dispose();

        var context = ((UnitOfWork)uow).Context;
        Assert.Throws<ObjectDisposedException>(() => { _ = context.Connection; });
    }

    /// <summary>
    /// Bug #5: Transaction 属性在 lock 内写入但 getter 无锁读取，需要 volatile 保障。
    /// </summary>
    [Fact]
    public void Transaction_ShouldBeVisible_AfterDemand_WithoutLock()
    {
        var mockConn = CreateOpenConnection();
        var mockTran = new Mock<IDbTransaction>();
        mockConn.Setup(c => c.BeginTransaction(It.IsAny<IsolationLevel>()))
            .Returns(mockTran.Object);

        var uow = new UnitOfWork(mockConn.Object, autoTransaction: false);
        uow.Demand(IsolationLevel.ReadCommitted);

        var context = ((UnitOfWork)uow).Context;
        var tran = context.Transaction;

        Assert.NotNull(tran);
        Assert.Same(mockTran.Object, tran);
    }

    /// <summary>
    /// Bug #5: Transaction 在 Rollback 清零后应可见为 null。
    /// </summary>
    [Fact]
    public void Transaction_ShouldBeNull_AfterRollback_WithoutLock()
    {
        var mockConn = CreateOpenConnection();
        var mockTran = new Mock<IDbTransaction>();
        mockConn.Setup(c => c.BeginTransaction(It.IsAny<IsolationLevel>()))
            .Returns(mockTran.Object);

        var uow = new UnitOfWork(mockConn.Object, autoTransaction: false);
        uow.Demand();
        uow.Rollback();

        var context = ((UnitOfWork)uow).Context;
        Assert.Null(context.Transaction);
    }

    private static Mock<IDbConnection> CreateOpenConnection()
    {
        var mock = new Mock<IDbConnection>();
        mock.Setup(c => c.State).Returns(ConnectionState.Open);
        return mock;
    }
}

/// <summary>
/// 用于测试的假仓储。
/// </summary>
public class FakeRepository : Repository<FakeEntity>
{
    public FakeRepository(IUnitOfWorkContext context) : base(context) { }
}

public class FakeEntity
{
    public long Id { get; set; }
    public string? Name { get; set; }
}
