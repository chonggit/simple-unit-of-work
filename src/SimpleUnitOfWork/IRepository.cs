namespace SimpleUnitOfWork
{
    /// <summary>
    /// 通用仓储接口，定义对实体的增删改查操作。
    /// </summary>
    public interface IRepository<T> where T : class, new()
    {
        /// <summary>
        /// 同步添加实体，返回新记录主键（long）。
        /// </summary>
        long Add(T entity);

        /// <summary>
        /// 异步添加实体，返回新记录主键（long）。
        /// </summary>
        Task<long> AddAsync(T entity);

        /// <summary>
        /// 同步删除实体，返回是否删除成功。
        /// </summary>
        bool Delete(T entity);

        /// <summary>
        /// 异步删除实体，返回是否删除成功。
        /// </summary>
        Task<bool> DeleteAsync(T entity);

        /// <summary>
        /// 获取所有实体的枚举（同步）。
        /// </summary>
        IEnumerable<T> GetAll();

        /// <summary>
        /// 获取所有实体的枚举（异步）。
        /// </summary>
        Task<IEnumerable<T>> GetAllAsync();

        /// <summary>
        /// 根据主键同步获取实体。
        /// </summary>
        T GetById(object id);

        /// <summary>
        /// 根据主键异步获取实体。
        /// </summary>
        Task<T> GetByIdAsync(object id);

        /// <summary>
        /// 同步更新实体，返回是否更新成功。
        /// </summary>
        bool Update(T entity);

        /// <summary>
        /// 异步更新实体，返回是否更新成功。
        /// </summary>
        Task<bool> UpdateAsync(T entity);
    }
}