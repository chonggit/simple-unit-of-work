using System.Data;
using Moq;
using SimpleUnitOfWork;

namespace SimpleUnitOfWork.Tests;

public class UnitOfWorkTests
{
    #region Constructor

    [Fact]
    public void Constructor_WithAutoTransactionTrue_ShouldCreateTransaction()
    {
        var mockConn = CreateOpenConnection();
        var mockTran = new Mock<IDbTransaction>();
        mockConn.Setup(c => c.BeginTransaction(It.IsAny<IsolationLevel>()))
            .Returns(mockTran.Object);

        using var uow = new UnitOfWork(mockConn.Object, autoTransaction: true);

        // Transaction 应该被自动创建
        Assert.NotNull(((UnitOfWork)uow).Context.Transaction);
    }

    [Fact]
    public void Constructor_WithAutoTransactionFalse_ShouldNotCreateTransaction()
    {
        var mockConn = CreateOpenConnection();

        using var uow = new UnitOfWork(mockConn.Object, autoTransaction: false);

        Assert.Null(((UnitOfWork)uow).Context.Transaction);
    }

    [Fact]
    public void Constructor_ShouldStoreIsolationLevel_ForLaterUse()
    {
        var mockConn = CreateOpenConnection();
        var mockTran = new Mock<IDbTransaction>();
        mockConn.Setup(c => c.BeginTransaction(It.IsAny<IsolationLevel>()))
            .Returns(mockTran.Object);

        var uow = new UnitOfWork(mockConn.Object, autoTransaction: false,
            isolationLevel: IsolationLevel.Serializable);

        // autoTransaction=false 时，isolationLevel 应被存储，后续 Demand() 使用
        uow.Demand();
        Assert.NotNull(((UnitOfWork)uow).Context.Transaction);
        mockConn.Verify(c => c.BeginTransaction(IsolationLevel.Serializable), Times.Once);
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenConnectionIsNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new UnitOfWork(null!, autoTransaction: false));
    }

    #endregion

    #region Demand

    [Fact]
    public void Demand_ShouldCreateTransaction()
    {
        var mockConn = CreateOpenConnection();
        var mockTran = new Mock<IDbTransaction>();
        mockConn.Setup(c => c.BeginTransaction(It.IsAny<IsolationLevel>()))
            .Returns(mockTran.Object);

        using var uow = new UnitOfWork(mockConn.Object, autoTransaction: false);
        uow.Demand(IsolationLevel.ReadCommitted);

        Assert.NotNull(((UnitOfWork)uow).Context.Transaction);
    }

    [Fact]
    public void Demand_ShouldThrow_WhenIsolationLevelMismatch()
    {
        var mockConn = CreateOpenConnection();
        var mockTran = new Mock<IDbTransaction>();
        mockConn.Setup(c => c.BeginTransaction(It.IsAny<IsolationLevel>()))
            .Returns(mockTran.Object);

        using var uow = new UnitOfWork(mockConn.Object, autoTransaction: false);
        uow.Demand(IsolationLevel.ReadCommitted);

        Assert.Throws<InvalidOperationException>(() =>
            uow.Demand(IsolationLevel.Serializable));
    }

    [Fact]
    public void Demand_ShouldNotThrow_WhenCalledTwiceWithSameLevel()
    {
        var mockConn = CreateOpenConnection();
        var mockTran = new Mock<IDbTransaction>();
        mockConn.Setup(c => c.BeginTransaction(It.IsAny<IsolationLevel>()))
            .Returns(mockTran.Object);

        using var uow = new UnitOfWork(mockConn.Object, autoTransaction: false);
        uow.Demand(IsolationLevel.ReadCommitted);

        var ex = Record.Exception(() => uow.Demand(IsolationLevel.ReadCommitted));
        Assert.Null(ex);
        // BeginTransaction 应该只被调用一次
        mockConn.Verify(c => c.BeginTransaction(It.IsAny<IsolationLevel>()), Times.Once);
    }

    [Fact]
    public void Demand_ShouldThrow_WhenConnectionHadUntransactedAccess()
    {
        var mockConn = CreateOpenConnection();
        var uow = new UnitOfWork(mockConn.Object, autoTransaction: false);

        // 访问 Connection 触发 _hasUntransactedAccess = true
        _ = ((UnitOfWork)uow).Context.Connection;

        Assert.Throws<InvalidOperationException>(() => uow.Demand());
    }

    [Fact]
    public void Demand_ShouldThrow_AfterDispose()
    {
        var mockConn = CreateOpenConnection();
        var uow = new UnitOfWork(mockConn.Object, autoTransaction: false);
        uow.Dispose();

        Assert.Throws<ObjectDisposedException>(() => uow.Demand());
    }

    [Fact]
    public void Demand_ShouldThrow_AfterCommit()
    {
        var mockConn = CreateOpenConnection();
        var mockTran = new Mock<IDbTransaction>();
        mockConn.Setup(c => c.BeginTransaction(It.IsAny<IsolationLevel>()))
            .Returns(mockTran.Object);

        using var uow = new UnitOfWork(mockConn.Object, autoTransaction: false);
        uow.Demand();
        uow.Commit();

        Assert.Throws<InvalidOperationException>(() => uow.Demand());
    }

    #endregion

    #region Commit

    [Fact]
    public void Commit_ShouldSucceed_AfterDemand()
    {
        var mockConn = CreateOpenConnection();
        var mockTran = new Mock<IDbTransaction>();
        mockConn.Setup(c => c.BeginTransaction(It.IsAny<IsolationLevel>()))
            .Returns(mockTran.Object);

        using var uow = new UnitOfWork(mockConn.Object, autoTransaction: false);
        uow.Demand();
        uow.Commit();

        mockTran.Verify(t => t.Commit(), Times.Once);
        mockTran.Verify(t => t.Dispose(), Times.Once);
        Assert.Null(((UnitOfWork)uow).Context.Transaction);
    }

    [Fact]
    public void Commit_ShouldThrow_WithoutDemand()
    {
        var mockConn = CreateOpenConnection();
        using var uow = new UnitOfWork(mockConn.Object, autoTransaction: false);

        Assert.Throws<InvalidOperationException>(() => uow.Commit());
    }

    [Fact]
    public void Commit_ShouldThrow_WhenCalledTwice()
    {
        var mockConn = CreateOpenConnection();
        var mockTran = new Mock<IDbTransaction>();
        mockConn.Setup(c => c.BeginTransaction(It.IsAny<IsolationLevel>()))
            .Returns(mockTran.Object);

        using var uow = new UnitOfWork(mockConn.Object, autoTransaction: false);
        uow.Demand();
        uow.Commit();

        Assert.Throws<InvalidOperationException>(() => uow.Commit());
    }

    [Fact]
    public void Commit_ShouldThrow_AfterDispose()
    {
        var mockConn = CreateOpenConnection();
        var uow = new UnitOfWork(mockConn.Object, autoTransaction: false);
        uow.Dispose();

        Assert.Throws<ObjectDisposedException>(() => uow.Commit());
    }

    #endregion

    #region Rollback

    [Fact]
    public void Rollback_ShouldSucceed_AfterDemand()
    {
        var mockConn = CreateOpenConnection();
        var mockTran = new Mock<IDbTransaction>();
        mockConn.Setup(c => c.BeginTransaction(It.IsAny<IsolationLevel>()))
            .Returns(mockTran.Object);

        using var uow = new UnitOfWork(mockConn.Object, autoTransaction: false);
        uow.Demand();
        uow.Rollback();

        mockTran.Verify(t => t.Rollback(), Times.Once);
        mockTran.Verify(t => t.Dispose(), Times.Once);
        Assert.Null(((UnitOfWork)uow).Context.Transaction);
    }

    [Fact]
    public void Rollback_ShouldThrow_WithoutDemand()
    {
        var mockConn = CreateOpenConnection();
        using var uow = new UnitOfWork(mockConn.Object, autoTransaction: false);

        Assert.Throws<InvalidOperationException>(() => uow.Rollback());
    }

    [Fact]
    public void Rollback_ShouldThrow_WhenCalledTwice()
    {
        var mockConn = CreateOpenConnection();
        var mockTran = new Mock<IDbTransaction>();
        mockConn.Setup(c => c.BeginTransaction(It.IsAny<IsolationLevel>()))
            .Returns(mockTran.Object);

        using var uow = new UnitOfWork(mockConn.Object, autoTransaction: false);
        uow.Demand();
        uow.Rollback();

        Assert.Throws<InvalidOperationException>(() => uow.Rollback());
    }

    #endregion

    #region Dispose

    [Fact]
    public void Dispose_ShouldRollback_WhenUncommittedTransactionExists()
    {
        var mockConn = CreateOpenConnection();
        var mockTran = new Mock<IDbTransaction>();
        mockConn.Setup(c => c.BeginTransaction(It.IsAny<IsolationLevel>()))
            .Returns(mockTran.Object);

        var uow = new UnitOfWork(mockConn.Object, autoTransaction: false);
        uow.Demand();
        uow.Dispose();

        // Dispose 应该回滚未提交的事务
        mockTran.Verify(t => t.Rollback(), Times.Once);
    }

    [Fact]
    public void Dispose_ShouldNotThrow_WithoutTransaction()
    {
        var mockConn = CreateOpenConnection();
        var uow = new UnitOfWork(mockConn.Object, autoTransaction: false);

        var ex = Record.Exception(() => uow.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public void Dispose_ShouldDisposeConnection()
    {
        var mockConn = CreateOpenConnection();
        var uow = new UnitOfWork(mockConn.Object, autoTransaction: false);
        uow.Dispose();

        mockConn.Verify(c => c.Dispose(), Times.AtLeastOnce);
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        var mockConn = CreateOpenConnection();
        var uow = new UnitOfWork(mockConn.Object, autoTransaction: false);
        uow.Dispose();

        var ex = Record.Exception(() => uow.Dispose());
        Assert.Null(ex);
    }

    #endregion

    #region Create Factory

    [Fact]
    public void Create_ShouldReturnUnitOfWork_WithTransaction()
    {
        var mockConn = CreateOpenConnection();
        var mockTran = new Mock<IDbTransaction>();
        mockConn.Setup(c => c.BeginTransaction(It.IsAny<IsolationLevel>()))
            .Returns(mockTran.Object);
        UnitOfWork.SetConnectionFactory(() => mockConn.Object);

        using var uow = UnitOfWork.Create();

        Assert.NotNull(uow);
        Assert.NotNull(((UnitOfWork)uow).Context.Transaction);
    }

    [Fact]
    public void Create_WithAutoTransactionFalse_ShouldReturnUnitOfWork_WithoutTransaction()
    {
        var mockConn = CreateOpenConnection();
        UnitOfWork.SetConnectionFactory(() => mockConn.Object);

        using var uow = UnitOfWork.Create(autoTransaction: false);

        Assert.NotNull(uow);
        Assert.Null(((UnitOfWork)uow).Context.Transaction);
    }

    [Fact]
    public void Create_ShouldThrow_WhenFactoryNotSet()
    {
        // 设置一个会抛出异常的工厂，模拟"未设置"的行为
        // 实际上 SetConnectionFactory 要求非 null，我们需要一个新方式来测试
        // 这里验证正常流程：设置 null-returning 工厂
        UnitOfWork.SetConnectionFactory(() => null!);
        Assert.Throws<InvalidOperationException>(() => UnitOfWork.Create());
    }

    [Fact]
    public void Create_WithIsolationLevel_ShouldUseSpecifiedLevel()
    {
        var mockConn = CreateOpenConnection();
        var mockTran = new Mock<IDbTransaction>();
        mockConn.Setup(c => c.BeginTransaction(It.IsAny<IsolationLevel>()))
            .Returns(mockTran.Object);
        UnitOfWork.SetConnectionFactory(() => mockConn.Object);

        using var uow = UnitOfWork.Create(autoTransaction: true,
            isolationLevel: IsolationLevel.Serializable);

        mockConn.Verify(c => c.BeginTransaction(IsolationLevel.Serializable), Times.Once);
    }

    #endregion

    #region GetRepository

    [Fact]
    public void GetRepository_ShouldReturnRepository_WithValidType()
    {
        var mockConn = CreateOpenConnection();
        using var uow = new UnitOfWork(mockConn.Object, autoTransaction: false);

        var repo = uow.GetRepository<FakeRepository>();

        Assert.NotNull(repo);
        Assert.IsType<FakeRepository>(repo);
    }

    [Fact]
    public void GetRepository_ShouldThrow_WhenTypeHasNoValidConstructor()
    {
        var mockConn = CreateOpenConnection();
        using var uow = new UnitOfWork(mockConn.Object, autoTransaction: false);

        Assert.Throws<InvalidOperationException>(() =>
            uow.GetRepository<RepositoryWithoutConstructor>());
    }

    #endregion

    #region SetConnectionFactory

    [Fact]
    public void SetConnectionFactory_ShouldThrow_WhenFactoryIsNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            UnitOfWork.SetConnectionFactory(null!));
    }

    #endregion

    #region Integration: Full lifecycle

    [Fact]
    public void FullLifecycle_AutoTransaction_Commit()
    {
        var mockConn = CreateOpenConnection();
        var mockTran = new Mock<IDbTransaction>();
        mockConn.Setup(c => c.BeginTransaction(It.IsAny<IsolationLevel>()))
            .Returns(mockTran.Object);

        using var uow = new UnitOfWork(mockConn.Object, autoTransaction: true);
        var repo = uow.GetRepository<FakeRepository>();

        Assert.NotNull(repo);
        uow.Commit();

        mockTran.Verify(t => t.Commit(), Times.Once);
    }

    [Fact]
    public void FullLifecycle_ManualTransaction_Rollback()
    {
        var mockConn = CreateOpenConnection();
        var mockTran = new Mock<IDbTransaction>();
        mockConn.Setup(c => c.BeginTransaction(It.IsAny<IsolationLevel>()))
            .Returns(mockTran.Object);

        using var uow = new UnitOfWork(mockConn.Object, autoTransaction: false);
        uow.Demand(IsolationLevel.ReadCommitted);
        var repo = uow.GetRepository<FakeRepository>();

        Assert.NotNull(repo);
        uow.Rollback();

        mockTran.Verify(t => t.Rollback(), Times.Once);
    }

    [Fact]
    public void FullLifecycle_NoTransaction_Dispose()
    {
        var mockConn = CreateOpenConnection();

        var uow = new UnitOfWork(mockConn.Object, autoTransaction: false);
        var repo = uow.GetRepository<FakeRepository>();

        Assert.NotNull(repo);
        var ex = Record.Exception(() => uow.Dispose());
        Assert.Null(ex); // Dispose 不应抛异常
    }

    #endregion

    #region Helpers

    private static Mock<IDbConnection> CreateOpenConnection()
    {
        var mock = new Mock<IDbConnection>();
        mock.Setup(c => c.State).Returns(ConnectionState.Open);
        return mock;
    }

    #endregion
}

/// <summary>
/// 没有接受 IUnitOfWorkContext 的构造函数的类型，用于测试 GetRepository 错误路径。
/// </summary>
public class RepositoryWithoutConstructor
{
}
