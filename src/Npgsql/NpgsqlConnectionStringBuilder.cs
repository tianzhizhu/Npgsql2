using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data.Common;
using System.Diagnostics.Contracts;
using System.DirectoryServices;
using System.Linq;
using System.Reflection;
using System.Security.Principal;

namespace Npgsql
{
    /// <summary>
    /// Provides a simple way to create and manage the contents of connection strings used by
    /// the <see cref="NpgsqlConnection"/> class.
    /// </summary>
    public sealed class NpgsqlConnectionStringBuilder : DbConnectionStringBuilder
    {
        #region Fields

        static readonly Dictionary<string, PropertyInfo> _propertiesByKeyword;
        static readonly Dictionary<PropertyInfo, object> _propertyDefaults;

        #endregion

        #region Constructors

        /// <summary>
        /// Initializes a new instance of the NpgsqlConnectionStringBuilder class.
        /// </summary>
        public NpgsqlConnectionStringBuilder() { Init(); }

        /// <summary>
        /// Initializes a new instance of the NpgsqlConnectionStringBuilder class, optionally using ODBC rules for quoting values.
        /// </summary>
        /// <param name="useOdbcRules">true to use {} to delimit fields; false to use quotation marks.</param>
        public NpgsqlConnectionStringBuilder(bool useOdbcRules) : base(useOdbcRules) { Init(); }

        /// <summary>
        /// Initializes a new instance of the NpgsqlConnectionStringBuilder class and sets its <see cref="DbConnectionStringBuilder.ConnectionString"/>.
        /// </summary>
        public NpgsqlConnectionStringBuilder(string connectionString)
        {
            Init();
            ConnectionString = connectionString;
        }

        void Init()
        {
            foreach (var kv in _propertyDefaults) {
                kv.Key.SetValue(this, kv.Value);
                base.Clear();
            }
        }

        #endregion

        #region Static initialization

        static NpgsqlConnectionStringBuilder()
        {
            var properties = typeof(NpgsqlConnectionStringBuilder)
                .GetProperties()
                .Where(p => p.GetCustomAttribute<NpgsqlConnectionStringPropertyAttribute>() != null)
                .ToArray();

            Contract.Assume(properties.All(p => p.CanRead && p.CanWrite));

            _propertiesByKeyword = (
                from p in properties
                from t in new[] { p.Name }.Concat(p.GetCustomAttribute<NpgsqlConnectionStringPropertyAttribute>().AlternateNames).Select(
                    k => new { Property = p, Keyword = k }
                )
                select t
            ).ToDictionary(t => t.Keyword.ToUpperInvariant(), t => t.Property);

            _propertyDefaults = properties
                .Where(p => p.GetCustomAttribute<ObsoleteAttribute>() == null)
                .ToDictionary(
                p => p,
                p => p.GetCustomAttribute<DefaultValueAttribute>() != null
                    ? p.GetCustomAttribute<DefaultValueAttribute>().Value
                    : (p.PropertyType.IsValueType ? Activator.CreateInstance(p.PropertyType) : null)
            );
        }

        #endregion

        #region Non-static property handling

        /// <summary>
        /// Gets or sets the value associated with the specified key.
        /// </summary>
        /// <param name="keyword">The key of the item to get or set.</param>
        /// <returns>The value associated with the specified key.</returns>
        public override object this[string keyword]
        {
            get
            {
                var value = GetProperty(keyword).GetValue(this);
                return value ?? "";
            }
            set
            {
                if (value == null) {
                    Remove(keyword);
                    return;
                }

                var p = GetProperty(keyword);
                try {
                    object convertedValue;
                    if (p.PropertyType.IsEnum && value is string) {
                        convertedValue = Enum.Parse(p.PropertyType, (string)value);
                    } else {
                        convertedValue = Convert.ChangeType(value, Type.GetTypeCode(p.PropertyType));
                    }
                    p.SetValue(this, convertedValue);
                } catch (Exception e) {
                    throw new ArgumentException("Couldn't set " + keyword, keyword, e);
                }
            }
        }

        /// <summary>
        /// Removes the entry with the specified key from the DbConnectionStringBuilder instance.
        /// </summary>
        /// <param name="keyword">The key of the key/value pair to be removed from the connection string in this DbConnectionStringBuilder.</param>
        /// <returns><b>true</b> if the key existed within the connection string and was removed; <b>false</b> if the key did not exist.</returns>
        public override bool Remove(string keyword)
        {
            var p = GetProperty(keyword);
            var removed = base.ContainsKey(p.Name);
            // Note that string property setters call SetValue, which itself calls base.Remove().
            p.SetValue(this, _propertyDefaults[p]);
            base.Remove(p.Name);
            return removed;
        }

        /// <summary>
        /// Clears the contents of the <see cref="NpgsqlConnectionStringBuilder"/> instance.
        /// </summary>
        public override void Clear()
        {
            Contract.Assert(Keys != null);
            foreach (var k in Keys.Cast<string>().ToArray()) {
                Remove(k);
            }
        }

        /// <summary>
        /// Determines whether the <see cref="NpgsqlConnectionStringBuilder"/> contains a specific key.
        /// </summary>
        /// <param name="keyword">The key to locate in the <see cref="NpgsqlConnectionStringBuilder"/>.</param>
        /// <returns><b>true</b> if the <see cref="NpgsqlConnectionStringBuilder"/> contains an entry with the specified key; otherwise <b>false</b>.</returns>
        public override bool ContainsKey(string keyword)
        {
            if (keyword == null)
                throw new ArgumentNullException("keyword");
            Contract.EndContractBlock();

            return _propertiesByKeyword.ContainsKey(keyword.ToUpperInvariant());
        }

        PropertyInfo GetProperty(string keyword)
        {
            PropertyInfo p;
            if (!_propertiesByKeyword.TryGetValue(keyword.ToUpperInvariant(), out p)) {
                throw new ArgumentException("Keyword not supported: " + keyword, "keyword");
            }
            return p;
        }

        void SetValue(string keyword, object value)
        {
            if (value == null) {
                base.Remove(keyword);
            } else {
                base[keyword] = value;
            }
        }

        #endregion

        #region Properties - Connection

        /// <summary>
        /// The hostname or IP address of the PostgreSQL server to connect to.
        /// </summary>
#if !DNXCORE50
        [Category("Connection")]
        [DisplayName("Host")]
        [Description("The hostname or IP address of the PostgreSQL server to connect to")]
#endif
        [NpgsqlConnectionStringProperty("Server")]
        public string Host
        {
            get { return _host; }
            set
            {
                _host = value;
                // TODO: Replace literal name with nameof operator in C# 6.0
                SetValue("Server", value);
            }
        }
        string _host;

        /// <summary>
        /// The TCP/IP port of the PostgreSQL server.
        /// </summary>
#if !DNXCORE50
        [Category("Connection")]
        [DisplayName("Port")]
        [Description("The TCP/IP port of the PostgreSQL server")]
        [DefaultValue(NpgsqlConnection.DefaultPort)]
#endif
        [NpgsqlConnectionStringProperty]
        public int Port
        {
            get { return _port; }
            set
            {
                if (value <= 0)
                    throw new ArgumentOutOfRangeException("value", value, "Invalid port: " + value);
                Contract.EndContractBlock();

                _port = value;
                // TODO: Replace literal name with nameof operator in C# 6.0
                SetValue("Port", value);
            }
        }
        int _port;

        ///<summary>
        /// The PostgreSQL database to connect to.
        /// </summary>
#if !DNXCORE50
        [Category("Connection")]
        [DisplayName("Database")]
        [Description("The PostgreSQL database to connect to")]
#endif
        [NpgsqlConnectionStringProperty("DB")]
        public string Database
        {
            get { return _database; }
            set
            {
                _database = value;
                // TODO: Replace literal name with nameof operator in C# 6.0
                SetValue("Database", value);
            }
        }
        string _database;

        /// <summary>
        /// The username to connect with. Not required if using IntegratedSecurity.
        /// </summary>
#if !DNXCORE50
        [Category("Connection")]
        [DisplayName("Username")]
        [Description("The username to connect with. Not required if using IntegratedSecurity.")]
#endif
        [NpgsqlConnectionStringProperty("User Name", "UserId", "User Id", "UID")]
        public string Username
        {
            get
            {
#if !DNXCORE50
                if ((_integratedSecurity) && (String.IsNullOrEmpty(_username))) {
                    _username = GetIntegratedUserName();
                }
#endif

                return _username;
            }

            set
            {
                _username = value;
                // TODO: Replace literal name with nameof operator in C# 6.0
                SetValue("Username", value);
            }
        }
        string _username;

        /// <summary>
        /// The password to connect with. Not required if using IntegratedSecurity.
        /// </summary>
#if !DNXCORE50
        [Category("Connection")]
        [DisplayName("Password")]
        [Description("The password to connect with. Not required if using IntegratedSecurity.")]
        [PasswordPropertyText(true)]
#endif
        [NpgsqlConnectionStringProperty("PSW", "PWD")]
        public string Password
        {
            get { return _password; }
            set
            {
                _password = value;
                // TODO: Replace literal name with nameof operator in C# 6.0
                SetValue("Password", value);
            }
        }
        string _password;

        /// <summary>
        /// Whether to enlist in an ambient TransactionScope.
        /// </summary>
#if !DNXCORE50
        [Category("Connection")]
        [DisplayName("Enlist")]
        [Description("Whether to enlist in an ambient TransactionScope")]
#endif
        [NpgsqlConnectionStringProperty]
        public bool Enlist
        {
            get { return _enlist; }
            set
            {
                _enlist = value;
                // TODO: Replace literal name with nameof operator in C# 6.0
                SetValue("Enlist", value);
            }
        }
        bool _enlist;

        /// <summary>
        /// The optional application name parameter to be sent to the backend during connection initiation.
        /// </summary>
#if !DNXCORE50
        [Category("Connection")]
        [DisplayName("Application Name")]
        [Description("The optional application name parameter to be sent to the backend during connection initiation")]
#endif
        [NpgsqlConnectionStringProperty]
        public string ApplicationName
        {
            get { return _applicationName; }
            set
            {
                _applicationName = value;
                // TODO: Replace literal name with nameof operator in C# 6.0
                SetValue("ApplicationName", value);
            }
        }
        string _applicationName;

        /// <summary>
        /// Gets or sets the schema search path.
        /// </summary>
#if !DNXCORE50
        [Category("Connection")]
        [DisplayName("Search Path")]
        [Description("Gets or sets the schema search path.")]
#endif
        [NpgsqlConnectionStringProperty]
        public string SearchPath
        {
            get { return _searchpath; }
            set
            {
                _searchpath = value;
                // TODO: Replace literal name with nameof operator in C# 6.0
                SetValue("SearchPath", value);
            }
        }
        string _searchpath;

        #endregion

        #region Properties - Security

        /// <summary>
        /// Whether to use SSL/TLS to encrypt the connection.
        /// </summary>
#if !DNXCORE50
        [Category("Security")]
        [DisplayName("SSL")]
        [Description("Whether to use SSL/TLS to encrypt the connection")]
#endif
        [NpgsqlConnectionStringProperty]
        public bool SSL
        {
            get { return _ssl; }
            set
            {
                _ssl = value;
                // TODO: Replace literal name with nameof operator in C# 6.0
                SetValue("SSL", value);
            }
        }
        bool _ssl;

        /// <summary>
        /// Controls whether SSL is required, preferred, allowed or disabled, depending on server support.
        /// </summary>
#if !DNXCORE50
        [Category("Security")]
        [DisplayName("SSLMode")]
        [Description("Controls whether SSL is required, preferred, allowed or disabled, depending on server support")]
#endif
        [NpgsqlConnectionStringProperty]
        public SslMode SslMode
        {
            get { return _sslmode; }
            set
            {
                _sslmode = value;
                // TODO: Replace literal name with nameof operator in C# 6.0
                SetValue("SslMode", value);
            }
        }
        SslMode _sslmode;

        /// <summary>
        /// Npgsql uses its own internal implementation of TLS/SSL. Turn this on to use .NET SslStream instead.
        /// </summary>
#if !DNXCORE50
        [Category("Security")]
        [DisplayName("Use SSL Stream")]
        [Description("Npgsql uses its own internal implementation of TLS/SSL. Turn this on to use .NET SslStream instead.")]
#endif
        [NpgsqlConnectionStringProperty]
        public bool UseSslStream
        {
            get { return _useSslStream; }
            set
            {
                _useSslStream = value;
                // TODO: Replace literal name with nameof operator in C# 6.0
                SetValue("UseSslStream", value);
            }
        }
        bool _useSslStream;

        /// <summary>
        /// Whether to use Windows integrated security to log in.
        /// </summary>
#if !DNXCORE50
        [Category("Security")]
        [DisplayName("Integrated Security")]
        [Description("Whether to use Windows integrated security to log in")]
#endif
        [NpgsqlConnectionStringProperty("Integrated Security")]
        public bool IntegratedSecurity
        {
            get { return _integratedSecurity; }
            set
            {
#if !NET40
                if (value)
                    CheckIntegratedSecuritySupport();
#endif
                _integratedSecurity = value;
                // TODO: Replace literal name with nameof operator in C# 6.0
                SetValue("IntegratedSecurity", value);
            }
        }
        bool _integratedSecurity;

        /// <summary>
        /// The Kerberos service name to be used for authentication.
        /// </summary>
#if !DNXCORE50
        [Category("Security")]
        [DisplayName("Kerberos Service Name")]
        [Description("The Kerberos service name to be used for authentication")]
#endif
        [NpgsqlConnectionStringProperty("Krbsrvname")]
        public string KerberosServiceName
        {
            get { return _kerberosServiceName; }
            set
            {
                _kerberosServiceName = value;
                // TODO: Replace literal name with nameof operator in C# 6.0
                SetValue("KerberosServiceName", value);
            }
        }
        string _kerberosServiceName;

        /// <summary>
        /// The Kerberos realm to be used for authentication.
        /// </summary>
#if !DNXCORE50
        [Category("Security")]
        [DisplayName("Include Realm")]
        [Description("The Kerberos realm to be used for authentication")]
#endif
        [NpgsqlConnectionStringProperty]
        public bool IncludeRealm
        {
            get { return _includeRealm; }
            set
            {
                _includeRealm = value;
                // TODO: Replace literal name with nameof operator in C# 6.0
                SetValue("IncludeRealm", value);
            }
        }
        bool _includeRealm;

        #endregion

        #region Properties - Pooling

        /// <summary>
        /// Whether connection pooling should be used.
        /// </summary>
#if !DNXCORE50
        [Category("Pooling")]
        [DisplayName("Pooling")]
        [Description("Whether connection pooling should be used")]
        [DefaultValue(true)]
#endif
        [NpgsqlConnectionStringProperty]
        public bool Pooling
        {
            get { return _pooling; }
            set
            {
                _pooling = value;
                // TODO: Replace literal name with nameof operator in C# 6.0
                SetValue("Pooling", value);
            }
        }
        bool _pooling;

        /// <summary>
        /// The minimum connection pool size.
        /// </summary>
#if !DNXCORE50
        [Category("Pooling")]
        [DisplayName("Minimum Pool Size")]
        [Description("The minimum connection pool size")]
        [DefaultValue(1)]
#endif
        [NpgsqlConnectionStringProperty]
        public int MinPoolSize
        {
            get { return _minPoolSize; }
            set
            {
                if (value < 0 || value > NpgsqlConnectorPool.PoolSizeLimit)
                    throw new ArgumentOutOfRangeException("value", value, "MinPoolSize must be between 0 and " + NpgsqlConnectorPool.PoolSizeLimit);
                Contract.EndContractBlock();

                _minPoolSize = value;
                // TODO: Replace literal name with nameof operator in C# 6.0
                SetValue("MinPoolSize", value);
            }
        }
        int _minPoolSize;

        /// <summary>
        /// The maximum connection pool size.
        /// </summary>
#if !DNXCORE50
        [Category("Pooling")]
        [DisplayName("Maximum Pool Size")]
        [Description("The maximum connection pool size")]
        [DefaultValue(20)]
#endif
        [NpgsqlConnectionStringProperty]
        public int MaxPoolSize
        {
            get { return _maxPoolSize; }
            set
            {
                if (value < 0 || value > NpgsqlConnectorPool.PoolSizeLimit)
                    throw new ArgumentOutOfRangeException("value", value, "MaxPoolSize must be between 0 and " + NpgsqlConnectorPool.PoolSizeLimit);
                Contract.EndContractBlock();

                _maxPoolSize = value;
                // TODO: Replace literal name with nameof operator in C# 6.0
                SetValue("MaxPoolSize", value);
            }
        }
        int _maxPoolSize;

        /// <summary>
        /// The time to wait before closing unused connections in the pool if the count
        /// of all connections exeeds MinPoolSize.
        /// </summary>
        /// <remarks>
        /// If connection pool contains unused connections for ConnectionLifeTime seconds,
        /// the half of them will be closed. If there will be unused connections in a second
        /// later then again the half of them will be closed and so on.
        /// This strategy provide smooth change of connection count in the pool.
        /// </remarks>
        /// <value>The time (in seconds) to wait. The default value is 15 seconds.</value>
#if !DNXCORE50
        [Category("Pooling")]
        [DisplayName("Connection Lifetime")]
        [Description("The time to wait before closing unused connections in the pool if the count of all connections exeeds MinPoolSize.")]
        [DefaultValue(15)]
#endif
        [NpgsqlConnectionStringProperty]
        public int ConnectionLifeTime
        {
            get { return _connectionLifeTime; }
            set
            {
                _connectionLifeTime = value;
                // TODO: Replace literal name with nameof operator in C# 6.0
                SetValue("ConnectionLifeTime", value);
            }
        }
        int _connectionLifeTime;

        #endregion

        #region Properties - Timeouts

        /// <summary>
        /// The time to wait (in seconds) while trying to establish a connection before terminating the attempt and generating an error.
        /// Defaults to 15 seconds.
        /// </summary>
#if !DNXCORE50
        [Category("Timeouts")]
        [DisplayName("Timeout")]
        [Description("The time to wait (in seconds) while trying to establish a connection before terminating the attempt and generating an error.")]
        [DefaultValue(15)]
#endif
        [NpgsqlConnectionStringProperty]
        public int Timeout
        {
            get { return _timeout; }
            set
            {
                if (value < 0 || value > NpgsqlConnection.TimeoutLimit)
                    throw new ArgumentOutOfRangeException("value", value, "Timeout must be between 0 and " + NpgsqlConnection.TimeoutLimit);
                Contract.EndContractBlock();

                _timeout = value;
                // TODO: Replace literal name with nameof operator in C# 6.0
                SetValue("Timeout", value);
            }
        }
        int _timeout;

        /// <summary>
        /// The time to wait (in seconds) while trying to execute a command before terminating the attempt and generating an error.
        /// Defaults to 20 seconds.
        /// </summary>
#if !DNXCORE50
        [Category("Timeouts")]
        [DisplayName("Command Timeout")]
        [Description("The time to wait (in seconds) while trying to execute a command before terminating the attempt and generating an error")]
        [DefaultValue(NpgsqlCommand.DefaultTimeout)]
#endif
        [NpgsqlConnectionStringProperty]
        public int CommandTimeout
        {
            get { return _commandTimeout; }
            set
            {
                if (value < 0)
                    throw new ArgumentOutOfRangeException("value", value, "CommandTimeout can't be negative");
                Contract.EndContractBlock();

                _commandTimeout = value;
                // TODO: Replace literal name with nameof operator in C# 6.0
                SetValue("CommandTimeout", value);
            }
        }
        int _commandTimeout;

        /// <summary>
        /// The time to wait (in seconds) while trying to execute a an internal command before terminating the attempt and generating an error.
        /// </summary>
#if !DNXCORE50
        [Category("Timeouts")]
        [DisplayName("Internal Command Timeout")]
        [Description("The time to wait (in seconds) while trying to execute a an internal command before terminating the attempt and generating an error")]
#endif
        [NpgsqlConnectionStringProperty]
        public int? InternalCommandTimeout
        {
            get { return _internalCommandTimeout; }
            set
            {
                if (value.HasValue && value < NpgsqlConnector.MinimumInternalCommandTimeout && InternalCommandTimeout != 0)
                    throw new ArgumentOutOfRangeException("value", value, string.Format("InternalCommandTimeout must be null, >= {0} or 0 (infinite)", NpgsqlConnector.MinimumInternalCommandTimeout));
                Contract.EndContractBlock();

                _internalCommandTimeout = value;
                // TODO: Replace literal name with nameof operator in C# 6.0
                SetValue("InternalCommandTimeout", value);
            }
        }
        int? _internalCommandTimeout;

        /// <summary>
        /// Whether to have the backend enforce <see cref="CommandTimeout"/> and <see cref="InternalCommandTimeout"/>
        /// via the statement_timeout variable. Defaults to true.
        /// </summary>
#if !DNXCORE50
        [Category("Timeouts")]
        [DisplayName("Backend Timeouts")]
        [Description("Whether to have the backend enforce CommandTimeout and InternalCommandTimeout via the statement_timeout variable.")]
        [DefaultValue(true)]
#endif
        [NpgsqlConnectionStringProperty]
        public bool BackendTimeouts
        {
            get { return _backendTimeouts; }
            set
            {
                _backendTimeouts = value;
                // TODO: Replace literal name with nameof operator in C# 6.0
                SetValue("BackendTimeouts", value);
            }
        }
        bool _backendTimeouts;

        #endregion

        #region Properties - Advanced

        /// <summary>
        /// Whether to listen for notifications and report them between command activity.
        /// </summary>
#if !DNXCORE50
        [Category("Advanced")]
        [DisplayName("Sync Notification")]
        [Description("Whether to listen for notifications and report them between command activity.")]
#endif
        [NpgsqlConnectionStringProperty]
        public bool SyncNotification
        {
            get { return _syncNotification; }
            set
            {
                _syncNotification = value;
                // TODO: Replace literal name with nameof operator in C# 6.0
                SetValue("SyncNotification", value);
            }
        }
        bool _syncNotification;

        /// <summary>
        /// The number of seconds of connection inactivity before Npgsql sends a keepalive query.
        /// Set to 0 (the default) to disable.
        /// </summary>
#if !DNXCORE50
        [Category("Advanced")]
        [DisplayName("Keepalive")]
        [Description("The number of seconds of connection inactivity before Npgsql sends a keepalive query.")]
#endif
        [NpgsqlConnectionStringProperty]
        public int KeepAlive
        {
            get { return _keepAlive; }
            set
            {
                if (value < 0)
                    throw new ArgumentOutOfRangeException("value", value, "KeepAlive can't be negative");
                Contract.EndContractBlock();

                _keepAlive = value;
                // TODO: Replace literal name with nameof operator in C# 6.0
                SetValue("KeepAlive", value);
            }
        }
        int _keepAlive;

        /// <summary>
        /// Gets or sets the buffer size.
        /// </summary>
#if !DNXCORE50
        [Category("Advanced")]
        [DisplayName("Buffer Size")]
        [Description("Determines the size of the internal buffer Npgsql uses when reading or writing. Increasing may improve performance if transferring large values from the database.")]
        [DefaultValue(NpgsqlBuffer.DefaultBufferSize)]
#endif
        [NpgsqlConnectionStringProperty]
        public int BufferSize
        {
            get { return _bufferSize; }
            set
            {
                _bufferSize = value;
                // TODO: Replace literal name with nameof operator in C# 6.0
                SetValue("BufferSize", value);
            }
        }
        int _bufferSize;

        #endregion

        #region Properties - Compatibility

        /// <summary>
        /// A compatibility mode for special PostgreSQL server types.
        /// </summary>
#if !DNXCORE50
        [Category("Compatibility")]
        [DisplayName("Server Compatibility Mode")]
        [Description("A compatibility mode for special PostgreSQL server types")]
#endif
        [NpgsqlConnectionStringProperty]
        public ServerCompatibilityMode ServerCompatibilityMode
        {
            get { return _serverCompatibilityMode; }
            set
            {
                _serverCompatibilityMode = value;
                // TODO: Replace literal name with nameof operator in C# 6.0
                SetValue("ServerCompatibilityMode", value);
            }
        }
        ServerCompatibilityMode _serverCompatibilityMode;

        /// <summary>
        /// Makes MaxValue and MinValue timestamps and dates readable as infinity and negative infinity.
        /// </summary>
#if !DNXCORE50
        [Category("Compatibility")]
        [DisplayName("Convert Infinity DateTime")]
        [Description("Makes MaxValue and MinValue timestamps and dates readable as infinity and negative infinity")]
#endif
        [NpgsqlConnectionStringProperty]
        public bool ConvertInfinityDateTime
        {
            get { return _convertInfinityDateTime; }
            set
            {
                _convertInfinityDateTime = value;
                // TODO: Replace literal name with nameof operator in C# 6.0
                SetValue("ConvertInfinityDateTime", value);
            }
        }
        bool _convertInfinityDateTime;

        #endregion

        #region Properties - Obsolete

        /// <summary>
        /// Obsolete, see https://github.com/npgsql/Npgsql/wiki/PreloadReader-Removal
        /// </summary>
#if !DNXCORE50
        [Category("Obsolete")]
        [DisplayName("Preload Reader")]
        [Description("Obsolete, see https://github.com/npgsql/Npgsql/wiki/PreloadReader-Removal")]
#endif
        [NpgsqlConnectionStringProperty]
        [Obsolete]
        public bool PreloadReader
        {
            get { return false; }
            set { throw new NotSupportedException("The PreloadReader parameter is no longer supported. Please see https://github.com/npgsql/Npgsql/wiki/PreloadReader-Removal"); }
        }

        /// <summary>
        /// Obsolete, see https://github.com/npgsql/Npgsql/wiki/UseExtendedTypes-Removal
        /// </summary>
#if !DNXCORE50
        [Category("Obsolete")]
        [DisplayName("Use Extended Types")]
        [Description("Obsolete, see https://github.com/npgsql/Npgsql/wiki/UseExtendedTypes-Removal")]
#endif
        [NpgsqlConnectionStringProperty]
        [Obsolete]
        public bool UseExtendedTypes
        {
            get { return false; }
            set { throw new NotSupportedException("The UseExtendedTypes parameter is no longer supported. Please see https://github.com/npgsql/Npgsql/wiki/UseExtendedTypes-Removal"); }
        }

        #endregion

        #region Integrated security support

        /// <summary>
        /// No integrated security if we're on mono and .NET 4.5 because of ClaimsIdentity,
        /// see https://github.com/npgsql/Npgsql/issues/133
        /// </summary>
        static void CheckIntegratedSecuritySupport()
        {
            if (Type.GetType("Mono.Runtime") != null)
                throw new NotSupportedException("IntegratedSecurity is currently unsupported on mono and .NET 4.5 (see https://github.com/npgsql/Npgsql/issues/133)");
        }

#if !DNXCORE50

        class CachedUpn
        {
            public string Upn;
            public DateTime ExpiryTimeUtc;
        }

        static Dictionary<SecurityIdentifier, CachedUpn> cachedUpns = new Dictionary<SecurityIdentifier, CachedUpn>();

        private string GetIntegratedUserName()
        {
            // Side note: This maintains the hack fix mentioned before for https://github.com/npgsql/Npgsql/issues/133.
            // In a nutshell, starting with .NET 4.5 WindowsIdentity inherits from ClaimsIdentity
            // which doesn't exist in mono, and calling a WindowsIdentity method bombs.
            // The workaround is that this function that actually deals with WindowsIdentity never
            // gets called on mono, so never gets JITted and the problem goes away.

            // Gets the current user's username for integrated security purposes
            WindowsIdentity identity = WindowsIdentity.GetCurrent();
            CachedUpn cachedUpn = null;
            string upn = null;

            // Check to see if we already have this UPN cached
            lock (cachedUpns) {
                if (cachedUpns.TryGetValue(identity.User, out cachedUpn)) {
                    if (cachedUpn.ExpiryTimeUtc > DateTime.UtcNow)
                        upn = cachedUpn.Upn;
                    else
                        cachedUpns.Remove(identity.User);
                }
            }

            try {
                if (upn == null) {
                    // Try to get the user's UPN in its correct case; this is what the
                    // server will need to verify against a Kerberos/SSPI ticket

                    // First, find a domain server we can talk to
                    string domainHostName;

                    using (DirectoryEntry rootDse = new DirectoryEntry("LDAP://rootDSE") { AuthenticationType = AuthenticationTypes.Secure }) {
                        domainHostName = (string)rootDse.Properties["dnsHostName"].Value;
                    }

                    // Query the domain server by the current user's SID
                    using (DirectoryEntry entry = new DirectoryEntry("LDAP://" + domainHostName) { AuthenticationType = AuthenticationTypes.Secure }) {
                        DirectorySearcher search = new DirectorySearcher(entry,
                            "(objectSid=" + identity.User.Value + ")", new string[] { "userPrincipalName" });

                        SearchResult result = search.FindOne();

                        upn = (string)result.Properties["userPrincipalName"][0];
                    }
                }

                if (cachedUpn == null) {
                    // Save this value
                    cachedUpn = new CachedUpn() { Upn = upn, ExpiryTimeUtc = DateTime.UtcNow.AddHours(3.0) };

                    lock (cachedUpns) {
                        cachedUpns[identity.User] = cachedUpn;
                    }
                }

                string[] upnParts = upn.Split('@');

                if (_includeRealm) {
                    // Make it Kerberos-y by uppercasing the realm part
                    return upnParts[0] + "@" + upnParts[1].ToUpperInvariant();
                } else {
                    return upnParts[0];
                }
            } catch {
                // Querying the directory failed, so return the SAM name
                // (which probably won't work, but it's better than nothing)
                return identity.Name.Split('\\')[1];
            }
        }

#endif

        #endregion

        #region Misc

        internal NpgsqlConnectionStringBuilder Clone()
        {
            return new NpgsqlConnectionStringBuilder(ConnectionString);
        }

        #endregion

        #region Attributes

        [AttributeUsage(AttributeTargets.Property)]
        class NpgsqlConnectionStringPropertyAttribute : Attribute
        {
            internal string[] AlternateNames { get; private set; }
            static string[] Empty = new string[0];

            internal NpgsqlConnectionStringPropertyAttribute()
            {
                AlternateNames = Empty;
            }

            internal NpgsqlConnectionStringPropertyAttribute(params string[] alternateNames)
            {
                AlternateNames = alternateNames;
            }
        }

        #endregion
    }

    #region Enums

    /// <summary>
    /// An option specified in the connection string that activates special compatibility features.
    /// </summary>
    public enum ServerCompatibilityMode
    {
        /// <summary>
        /// No special server compatibility mode is active
        /// </summary>
        None,
        /// <summary>
        /// The server is an Amazon Redshift instance.
        /// </summary>
        Redshift,
    }

    /// <summary>
    /// Specifies how to manage SSL.
    /// </summary>
    public enum SslMode
    {
        /// <summary>
        /// SSL is disabled. If the server requires SSL, the connection will fail.
        /// </summary>
        Disable,
        /// <summary>
        /// Allow SSL connections if the server requires them.
        /// </summary>
        Allow,
        /// <summary>
        /// Prefer SSL connections if the server allows them, but allow connections without SSL.
        /// </summary>
        Prefer,
        /// <summary>
        /// Fail the connection if the server doesn't suppotr SSL.
        /// </summary>
        Require,
    }

    #endregion
}
