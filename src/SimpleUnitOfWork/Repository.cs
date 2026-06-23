using Dapper.Contrib.Extensions;
using System.Data;

namespace SimpleUnitOfWork
{
    /// <summary>
    /// 通用仓储实现，基于 Dapper.Contrib 提供增删改查操作。
    /// </summary>
    public class Repository<T> : IRepository<T> where T : class, new()
    {
        private readonly IUnitOfWork _unitOfWork;

        /// <summary>
        /// 构造函数，注入工作单元以使用共享连接和事务。
        /// </summary>
        public Repository(IUnitOfWork unitOfWork)
        {
            ArgumentNullException.ThrowIfNull(unitOfWork);
            _unitOfWork = unitOfWork;
        }

        /// <summary>
        /// 当前数据库连接，来自注入的工作单元。
        /// </summary>
        protected virtual IDbConnection Connection => _unitOfWork.Connection;

        /// <summary>
        /// 当前事务，来自注入的工作单元。
        /// </summary>
        protected virtual IDbTransaction Transaction => _unitOfWork.Transaction;

        /// <summary>
        /// 命令超时时间（秒），来自注入的工作单元。
        /// </summary>
        protected virtual int CommandTimeout => _unitOfWork.CommandTimeout;

        /// <summary>
        /// 同步添加实体并返回新记录的主键。
        /// </summary>
        public virtual long Add(T entity)
        {
          return Connection.Insert(entity, Transaction, CommandTimeout);
        }

        /// <summary>
        /// 异步添加实体并返回新记录的主键。
        /// </summary>
        public virtual async Task<long> AddAsync(T entity)
        {
            return await Connection.InsertAsync(entity, Transaction, CommandTimeout);
        }

        /// <summary>
        /// 同步更新实体，返回是否成功。
        /// </summary>
        public virtual bool Update(T entity)
        {
            return Connection.Update(entity, Transaction, CommandTimeout);
        }

        /// <summary>
        /// 异步更新实体，返回是否成功。
        /// </summary>
        public virtual async Task<bool> UpdateAsync(T entity)
        {
            return await Connection.UpdateAsync(entity, Transaction, CommandTimeout);
        }

        /// <summary>
        /// 同步删除实体，返回是否成功。
        /// </summary>
        public virtual bool Delete(T entity)
        {
            return Connection.Delete(entity, Transaction, CommandTimeout);
        }

        /// <summary>
        /// 异步删除实体，返回是否成功。
        /// </summary>
        public virtual async Task<bool> DeleteAsync(T entity)
        {
            return await Connection.DeleteAsync(entity, Transaction, CommandTimeout);
        }

        /// <summary>
        /// 同步根据主键获取实体。
        /// </summary>
        public virtual T GetById(object id)
        {
            return Connection.Get<T>(id, Transaction, CommandTimeout);
        }

        /// <summary>
        /// 异步根据主键获取实体。
        /// </summary>
        public virtual async Task<T> GetByIdAsync(object id)
        {
            return await Connection.GetAsync<T>(id, Transaction, CommandTimeout);
        }

        /// <summary>
        /// 同步获取所有实体集合。
        /// </summary>
        public virtual IEnumerable<T> GetAll()
        {
            return Connection.GetAll<T>(Transaction, CommandTimeout);
        }

        /// <summary>
        /// 异步获取所有实体集合。
        /// </summary>
        public virtual async Task<IEnumerable<T>> GetAllAsync()
        {
            return await Connection.GetAllAsync<T>(Transaction, CommandTimeout);
        }
    }
}
