﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using Stardust.Paradox.Data.CodeGeneration;
using Stardust.Paradox.Data.Internals;
using Stardust.Paradox.Data.Traversals;
using Stardust.Paradox.Data.Tree;
using Stardust.Particles;

namespace Stardust.Paradox.Data
{
    public abstract class GraphContextBase : IGraphContext
    {

        private readonly IGremlinLanguageConnector _connector;
        private readonly IServiceProvider _resolver;
        internal static DualDictionary<Type, string> _dataSetLabelMapping = new DualDictionary<Type, string>();
        private static bool _initialized;
        private static readonly object lockObject = new object();
        private ConcurrentDictionary<string, GraphDataEntity> _trackedEntities = new ConcurrentDictionary<string, GraphDataEntity>();


        protected GraphContextBase(IGremlinLanguageConnector connector, IServiceProvider resolver)
        {
            _connector = connector;
            _resolver = resolver;
            if (_initialized) return;
            lock (lockObject)
            {
                if (_initialized) return;
                BuildModel();
                _initialized = true;
            }
        }

        private void BuildModel()
        {
            InitializeModel(new GraphConfiguration(this));
        }

        public void Delete<T>(T toBeDeleted) where T : IVertex
        {
            var i = toBeDeleted as GraphDataEntity;
            i.Delete();
        }

        public void ResetChanges<T>(T entityToReset) where T : IVertex
        {
            var i = entityToReset as GraphDataEntity;
            i.Reset(i.IsNew);
        }

        /// <summary>
        /// Register all vertecies that should be used with this context.
        /// </summary>
        /// <param name="configuration"></param>
        /// <returns>true if overridden</returns>
        protected abstract bool InitializeModel(IGraphConfiguration configuration);

        private T Create<T>()
        {
            var t = ActivatorUtilities.CreateInstance(_resolver, GraphJsonConverter.GetImplementationType(typeof(T)));
            if (t != null) return (T)t;
            return default(T);
        }

        public T CreateEntity<T>(string id) where T : IVertex
        {


            var item =Create<T>();
            var i = item as GraphDataEntity;
            i._entityKey = id;
            i.Reset(true);
            i.SetContext(this);
            _trackedEntities.TryAdd(id, i);
            return item;
        }

        public GremlinQuery V<T>() where T : IVertex
        {
            return GremlinFactory.G.V().HasLabel(_dataSetLabelMapping[typeof(T)]);
        }

        public GremlinQuery V<T>(string id) where T : IVertex
        {
            return GremlinFactory.G.V(id).HasLabel(_dataSetLabelMapping[typeof(T)]);
        }

        public async Task<T> VAsync<T>(string id) where T : IVertex
        {
            if (_trackedEntities.TryGetValue(id, out var i)) return (T)(object)i;
            return await ConvertTo<T>(await _connector.V(id).ExecuteAsync(), true);
        }

        public async Task<T> GetOrCreate<T>(string id) where T : IVertex
        {
            var i = await VAsync<T>(id);
            if (i == null)
                return CreateEntity<T>(id);
            return i;
        }

        public Task<IEnumerable<T>> VAsync<T>(Func<GremlinContext, GremlinQuery> g) where T : IVertex
        {
            return VAsync<T>(g.Invoke(new GremlinContext(_connector)));
        }

        public async Task<IEnumerable<T>> VAsync<T>(GremlinQuery g) where T : IVertex
        {
            var v = await g.ExecuteAsync();
            return v.Select(d => GetItemValue<T>((object)d)).ToList();
        }

        internal T GetItemValue<T>(object o) where T : IVertex
        {
            try
            {
                var d = o as dynamic;
                if (_trackedEntities.TryGetValue((string)d.id, out var i)) return (T)(object)i;
                return Convert<T>(d).Result;
            }
            catch (Exception ex)
            {
                Logging.DebugMessage($"{o?.ToString() ?? "{null}"}");
                throw;
            }
        }

        private async Task<T> ConvertTo<T>(IEnumerable<dynamic> enumerable, bool doEagerLoad = false) where T : IVertex
        {

            var d = enumerable.SingleOrDefault();
            if (d == null) return default(T);
            return await Convert<T>(d, doEagerLoad);
        }

        private async Task<T> Convert<T>(dynamic d, bool doEagerLoad = false) where T : IVertex
        {
            var item = Create<T>();
            var i = item as GraphDataEntity;
            i._entityKey = d.id;

            foreach (var p in d.properties as JObject)
            {
                var y = p.Value.ToObject<Property[]>();
                TransferData(item, p.Key.ToPascalCase(), y.First().Value);
            }
            i.Reset(false);
            i.SetContext(this);
            await i.Eager(doEagerLoad);
            _trackedEntities.TryAdd(i._entityKey, i);
            return item;
        }

        public T MakeInstance<T>(dynamic d) where T : IVertex
        {
            var item = Create<T>();
            var i = item as GraphDataEntity;
            i._entityKey = d.id;

            foreach (var p in d.properties as JObject)
            {
                var y = p.Value.ToObject<Property[]>();
                TransferData(item, p.Key.ToPascalCase(), y.First().Value);
            }
            i.Reset(false);
            i.SetContext(this);
            _trackedEntities.TryAdd(i._entityKey, i);
            return item;
        }

        public async Task SaveChangesAsync()
        {
            SavingChanges?.Invoke(this, new SaveEventArgs { TrackedItems = _trackedEntities.Values });
            try
            {
                var deleted = new List<GraphDataEntity>();
                foreach (var graphDataEntity in from i in _trackedEntities where i.Value.IsDirty select i)
                {
                    await _connector.ExecuteAsync(graphDataEntity.Value.GetUpdateStatement());
                    foreach (var edges in graphDataEntity.Value.GetEdges())
                    {
                        await edges.SaveChangesAsync();
                    }
                    if (graphDataEntity.Value.IsDeleted)
                        deleted.Add(graphDataEntity.Value);

                }
                foreach (var graphDataEntity in from i in _trackedEntities where i.Value.IsDirty select i)
                {
                    foreach (var edges in graphDataEntity.Value.GetEdges())
                    {
                        await edges.SaveChangesAsync();
                    }
                    if (graphDataEntity.Value.IsDeleted)
                        deleted.Add(graphDataEntity.Value);

                }
                foreach (var graphDataEntity in deleted)
                {
                    _trackedEntities.TryRemove(graphDataEntity._entityKey, out var d);
                }
                foreach (var graphDataEntity in _trackedEntities)
                {
                    graphDataEntity.Value.Reset(false);
                }
            }
            catch (Exception ex)
            {
                SaveChangesError?.Invoke(this, new SaveEventArgs { TrackedItems = _trackedEntities.Values, Error = ex });
            }
            ChangesSaved?.Invoke(this, new SaveEventArgs { TrackedItems = _trackedEntities.Values });
        }

        public async Task<IEnumerable<dynamic>> ExecuteAsync<T>(Func<GremlinContext, GremlinQuery> func)
        {
            return await func.Invoke(new GremlinContext(_connector)).ExecuteAsync();
        }

        public async Task<IVertexTreeRoot<T>> GetTreeAsync<T>(string rootId, string edgeLabel, bool incommingEdge = false) where T : IVertex
        {
            IEnumerable<dynamic> c;
            if (!incommingEdge)
            {
                c = await _connector.V(rootId).Repeat(p => p.Out(edgeLabel)).Until(p => p.OutE().Count().Is(0))
                    .Tree().ExecuteAsync();
                //c =await _connector.V(rootId).Out(edgeLabel).Tree().ExecuteAsync();
            }
            else
            {
                c = await _connector.V(rootId)
                    .Repeat(p => p.__().In(edgeLabel))
                    .Until(p => p.InE(edgeLabel).Count().Is(0))
                    .Tree().ExecuteAsync();
                //c = await _connector.V(rootId).In(edgeLabel).Tree().ExecuteAsync();
            }
            return new VertexTreeRoot<T>(c, this);
        }

        public Task<IVertexTreeRoot<T>> GetTreeAsync<T>(string rootId, Expression<Func<T, object>> byProperty, bool incommingEdge = false) where T : IVertex
        {
            string propertyName = QueryFuncExt.GetEdgeLabel<T>(byProperty);
            return GetTreeAsync<T>(rootId, propertyName, incommingEdge);
        }

        public void Attatch<T>(T item)
        {
            var i = item as GraphDataEntity;
            if (i._entityKey.IsNullOrWhiteSpace()) i._entityKey = Guid.NewGuid().ToString();
            i.SetContext(this);
            _trackedEntities.TryAdd(i._entityKey, i);
        }

        public event SavingChangesHandler SavingChanges;
        public event SavingChangesHandler ChangesSaved;

        public event SavingChangesHandler SaveChangesError;

        private static readonly ConcurrentDictionary<string, Func<object, object>> getExpressionCache = new ConcurrentDictionary<string, Func<object, object>>();
        private static readonly ConcurrentDictionary<string, PropertyInfo> propertyInfos = new ConcurrentDictionary<string, PropertyInfo>();
        private static readonly ConcurrentDictionary<string, Action<object, object>> _setProppertyValueFunc = new ConcurrentDictionary<string, Action<object, object>>();
        private void TransferData(object item, string key, object value)
        {
            try
            {
                Action<object, object> action;
                if (!propertyInfos.TryGetValue(item.GetType() + "." + key, out var prop))
                {
                    prop = item.GetType().GetProperty(key,
                        BindingFlags.Public | BindingFlags.FlattenHierarchy | BindingFlags.Instance);
                    propertyInfos.TryAdd(item.GetType() + "." + key, prop);
                }
                if (prop.GetCustomAttribute<InlineSerializationAttribute>() != null)
                {
                    var v = GetValue(item, key) as IInlineCollection;
                    v.LoadFromTransferData(value?.ToString());
                    return;
                }
                if (!_setProppertyValueFunc.TryGetValue(item.GetType() + "." + key, out action))
                {


                    action = CreateSet(prop);
                    _setProppertyValueFunc.TryAdd(item.GetType() + "." + key, action);
                }

                if (prop.PropertyType == typeof(DateTime))
                    action.Invoke(item, new DateTime(long.Parse(value.ToString())));
                else if (prop.PropertyType == typeof(DateTime?))
                    action.Invoke(item, value == null ? (DateTime?)null : new DateTime(long.Parse(value?.ToString())));
                else
                    action.Invoke(item, value);
            }
            catch (Exception ex)
            {
                ex.Log($"Parent: {item?.ToString() ?? "{null}"} Key: {key} Value:{value?.ToString() ?? "{null}"}");
                throw;
            }
        }

        private static Action<object, object> CreateSet(PropertyInfo info)
        {
            try
            {
                var valueParameter = Expression.Parameter(typeof(object), "value");
                var instanceParameter = Expression.Parameter(typeof(object), "target");
                var member = Expression.Property(Expression.Convert(instanceParameter, info.DeclaringType), info);
                var assign = Expression.Assign(member, Expression.Convert(valueParameter, info.PropertyType));
                var lambda = Expression.Lambda<Action<object, object>>(Expression.Convert(assign, typeof(object)), instanceParameter, valueParameter);
                return lambda.Compile();
            }
            catch (Exception ex)
            {
                Logging.Exception(ex);
                throw;
            }
        }

        internal static IEdgeCollection TransferData(object item, string key)
        {
            var value = GetValue(item, key);
            return (IEdgeCollection)value;
        }

        private static object GetValue(object item, string key)
        {
            Func<object, object> action;
            PropertyInfo prop;
            if (!getExpressionCache.TryGetValue(item.GetType() + "." + key, out action))
            {
                prop = item.GetType().GetProperty(key,
                    BindingFlags.Public | BindingFlags.FlattenHierarchy | BindingFlags.Instance);
                propertyInfos.TryAdd(item.GetType() + "." + key, prop);
                action = CreateGet(prop);
                getExpressionCache.TryAdd(item.GetType() + "." + key, action);
            }
            propertyInfos.TryGetValue(item.GetType() + "." + key, out prop);
            var value = action.Invoke(item);
            return value;
        }

        public static Func<object, object> CreateGet(PropertyInfo property)
        {
            var instanceParameter = Expression.Parameter(typeof(object), "target");
            var member = Expression.Property(Expression.Convert(instanceParameter, property.DeclaringType), property);
            var lambda = Expression.Lambda<Func<object, object>>(Expression.Convert(member, typeof(object)), instanceParameter);
            return lambda.Compile();
        }

        protected IGraphSet<T> GraphSet<T>() where T : IVertex
        {
            return new GraphSet<T>(this);
        }
    }
}