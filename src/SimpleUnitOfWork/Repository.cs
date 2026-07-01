using Dapper.Contrib.Extensions;
using System.Data;

namespace SimpleUnitOfWork
{
    /// <summary>
    /// 通用仓储实现，基于 Dapper.Contrib 提供增删改查操作。
    /// </summary>
    public class Repository<T> : IRepository<T> where T : class, new()
    {
        private readonly IUnitOfWorkContext _context;

        /// <summary>
        /// 构造函数，注入工作单元上下文以使用共享连接和事务。
        /// </summary>
        /// <param name="context">工作单元上下文</param>
        public Repository(IUnitOfWorkContext context)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context), "工作单元上下文不能为空。");
            _context = context;
        }

        /// <summary>
        /// 当前数据库连接，来自注入的工作单元上下文。
        /// </summary>
        protected virtual IDbConnection Connection => _context.Connection;

        /// <summary>
        /// 当前事务，来自注入的工作单元上下文。
        /// </summary>
        protected virtual IDbTransaction Transaction => _context.Transaction;

        /// <summary>
        /// 命令超时时间（秒），来自注入的工作单元上下文。
        /// </summary>
        protected virtual int CommandTimeout => _context.CommandTimeout;

        // ---- CRUD 方法保持不变 ----

        /// <summary>
        /// 同步添加实体并返回新记录的主键。
        /// </summary>
        public virtual long Add(T entity) => Connection.Insert(entity, Transaction, CommandTimeout);

        /// <summary>
        /// 异步添加实体并返回新记录的主键。
        /// </summary>
        public virtual async Task<long> AddAsync(T entity) => await Connection.InsertAsync(entity, Transaction, CommandTimeout);

        /// <summary>
        /// 同步更新实体，返回是否成功。
        /// </summary>
        public virtual bool Update(T entity) => Connection.Update(entity, Transaction, CommandTimeout);

        /// <summary>
        /// 异步更新实体，返回是否成功。
        /// </summary>
        public virtual async Task<bool> UpdateAsync(T entity) => await Connection.UpdateAsync(entity, Transaction, CommandTimeout);

        /// <summary>
        /// 同步删除实体，返回是否成功。
        /// </summary>
        public virtual bool Delete(T entity) => Connection.Delete(entity, Transaction, CommandTimeout);

        /// <summary>
        /// 异步删除实体，返回是否成功。
        /// </summary>
        public virtual async Task<bool> DeleteAsync(T entity) => await Connection.DeleteAsync(entity, Transaction, CommandTimeout);

        /// <summary>
        /// 同步根据主键获取实体。
        /// </summary>
        public virtual T GetById(object id) => Connection.Get<T>(id, Transaction, CommandTimeout);

        /// <summary>
        /// 异步根据主键获取实体。
        /// </summary>
        public virtual async Task<T> GetByIdAsync(object id) => await Connection.GetAsync<T>(id, Transaction, CommandTimeout);

        /// <summary>
        /// 同步获取所有实体集合。
        /// </summary>
        public virtual IEnumerable<T> GetAll() => Connection.GetAll<T>(Transaction, CommandTimeout);

        /// <summary>
        /// 异步获取所有实体集合。
        /// </summary>
        public virtual async Task<IEnumerable<T>> GetAllAsync() => await Connection.GetAllAsync<T>(Transaction, CommandTimeout);
    }
}
