namespace Paradise.BT;

/// <summary>
/// .NET-friendly blackboard that implements the exact EntitiesBT interface shape.
/// </summary>
public struct Blackboard : IMutableBlackboard
{
    private BlackboardStorage? _storage;

    private BlackboardStorage Storage => _storage ?? (_storage = new BlackboardStorage());

    public bool HasData<T>() where T : struct => Storage.HasData(typeof(T));

    public T GetData<T>() where T : struct => Storage.GetData<T>();

    public ref T GetDataRef<T>() where T : struct => ref Storage.GetDataRef<T>();

    public bool HasData(Type type)
    {
        ThrowHelper.ThrowIfNull(type, nameof(type));
        return Storage.HasData(type);
    }

    public IntPtr GetDataPtrRO(Type type)
        => throw new NotSupportedException("Pointer-based blackboard access is not supported by Paradise.BT's managed runtime.");

    public IntPtr GetDataPtrRW(Type type)
        => throw new NotSupportedException("Pointer-based blackboard access is not supported by Paradise.BT's managed runtime.");

    public T GetObject<T>() where T : class => Storage.GetObject<T>();

    public void SetData<T>(T value) where T : struct => Storage.SetData(value);

    public void SetObject<T>(T value) where T : class => Storage.SetObject(value);

    public bool RemoveData<T>() where T : struct => Storage.RemoveData(typeof(T));

    public bool RemoveObject<T>() where T : class => Storage.RemoveObject(typeof(T));

    public bool Has(string key)
    {
        ThrowHelper.ThrowIfNull(key, nameof(key));
        return Storage.HasNamed(key);
    }

    public T Get<T>(string key)
    {
        ThrowHelper.ThrowIfNull(key, nameof(key));
        return Storage.GetNamed<T>(key);
    }

    public bool TryGet<T>(string key, out T value)
    {
        ThrowHelper.ThrowIfNull(key, nameof(key));
        return Storage.TryGetNamed(key, out value);
    }

    public void Set<T>(string key, T value)
    {
        ThrowHelper.ThrowIfNull(key, nameof(key));
        Storage.SetNamed(key, value);
    }

    public bool Remove(string key)
    {
        ThrowHelper.ThrowIfNull(key, nameof(key));
        return Storage.RemoveNamed(key);
    }

    private sealed class BlackboardStorage
    {
        private readonly Dictionary<Type, object> _structValues = new Dictionary<Type, object>();
        private readonly Dictionary<Type, object> _objectValues = new Dictionary<Type, object>();
        private readonly Dictionary<string, object?> _namedValues = new Dictionary<string, object?>(StringComparer.Ordinal);

        public bool HasData(Type type) => _structValues.ContainsKey(type);

        public T GetData<T>() where T : struct => GetDataRef<T>();

        public ref T GetDataRef<T>() where T : struct
        {
            if (!_structValues.TryGetValue(typeof(T), out object? value))
            {
                throw new KeyNotFoundException($"Blackboard does not contain data for '{typeof(T).FullName}'.");
            }

            return ref ((StructValueBox<T>)value).Value;
        }

        public void SetData<T>(T value) where T : struct
        {
            if (_structValues.TryGetValue(typeof(T), out object? existing))
            {
                ((StructValueBox<T>)existing).Value = value;
                return;
            }

            _structValues[typeof(T)] = new StructValueBox<T>(value);
        }

        public bool RemoveData(Type type) => _structValues.Remove(type);

        public T GetObject<T>() where T : class
        {
            if (!_objectValues.TryGetValue(typeof(T), out object? value))
            {
                throw new KeyNotFoundException($"Blackboard does not contain object data for '{typeof(T).FullName}'.");
            }

            return (T)value;
        }

        public void SetObject<T>(T value) where T : class
            => _objectValues[typeof(T)] = value ?? throw new ArgumentNullException(nameof(value));

        public bool RemoveObject(Type type) => _objectValues.Remove(type);

        public bool HasNamed(string key) => _namedValues.ContainsKey(key);

        public T GetNamed<T>(string key)
        {
            if (!_namedValues.TryGetValue(key, out object? value))
            {
                throw new KeyNotFoundException($"Blackboard does not contain a value for key '{key}'.");
            }

            if (value is T typed)
            {
                return typed;
            }

            if (value is null && default(T) is null)
            {
                return default!;
            }

            throw new InvalidCastException($"Blackboard value '{key}' cannot be cast to '{typeof(T).FullName}'.");
        }

        public bool TryGetNamed<T>(string key, out T value)
        {
            if (_namedValues.TryGetValue(key, out object? stored))
            {
                if (stored is T typed)
                {
                    value = typed;
                    return true;
                }

                if (stored is null && default(T) is null)
                {
                    value = default!;
                    return true;
                }
            }

            value = default!;
            return false;
        }

        public void SetNamed<T>(string key, T value) => _namedValues[key] = value;

        public bool RemoveNamed(string key) => _namedValues.Remove(key);
    }

    private sealed class StructValueBox<T> where T : struct
    {
        public StructValueBox(T value)
        {
            Value = value;
        }

        public T Value;
    }
}
