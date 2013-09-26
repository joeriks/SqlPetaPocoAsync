﻿/*
 * This project is a modification fo the single file PetaPoco.cs. All references to other
 * databases have been removed.
 * 
 * This is SqlServer specific, and all the calls are asynchronous.  You must use
 * async/away for all the data access methods.
 * 
 * This is all largely untested, and is the result of some heavy copying and pasting.
 * YMMV, caveat emptor, use at your own risk.
 * 
 * The original License applies to this modification, and I make no claims over it.
 * 
 * - andy guerrera |  @aguerrera | github.com/aguerrera
 */

/* 
 * ORIGINAL LICENCE
 * PetaPoco - A Tiny ORMish thing for your POCO's.
 * Copyright © 2011-2012 Topten Software.  All Rights Reserved.
 * 
 * Apache License 2.0 - http://www.toptensoftware.com/petapoco/license
 * 
 * Special thanks to Rob Conery (@robconery) for original inspiration (ie:Massive) and for 
 * use of Subsonic's T4 templates, Rob Sullivan (@DataChomp) for hard core DBA advice 
 * and Adam Schroder (@schotime) for lots of suggestions, improvements and Oracle support
 */


using SqlPetaPocoAsync.DatabaseTypes;
using SqlPetaPocoAsync.Internal;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.Data.Common;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SqlPetaPocoAsync
{
	/// <summary>
	/// The main SqlPetaPocoAsync Database class.  You can either use this class directly, or derive from it.
	/// </summary>
	public class Database : IDisposable
	{
		#region Constructors
		/// <summary>
		/// Construct a database using a supplied SqlConnection
		/// </summary>
		/// <param name="connection">The SqlConnection to use</param>
		/// <remarks>
		/// The supplied SqlConnection will not be closed/disposed by SqlPetaPocoAsync - that remains
		/// the responsibility of the caller.
		/// </remarks>
		public Database(SqlConnection connection)
		{
			_sharedConnection = connection;
			_connectionString = connection.ConnectionString;
			_sharedConnectionDepth = 2;		// Prevent closing external connection
			CommonConstruct();
		}

		/// <summary>
		/// Construct a database using a supplied connections string and optionally a provider name
		/// </summary>
		/// <param name="connectionString">The DB connection string</param>
		/// <remarks>
		/// SqlPetaPocoAsync will automatically close and dispose any connections it creates.
		/// </remarks>
		public Database(string connectionString)
		{
			_connectionString = connectionString;
            _factory = SqlClientFactory.Instance;
            CommonConstruct();
		}

		/// <summary>
		/// Provides common initialization for the various constructors
		/// </summary>
		private void CommonConstruct()
		{
			// Reset
			_transactionDepth = 0;
			EnableAutoSelect = true;
			EnableNamedParams = true;

			// If a provider name was supplied, get the IDbProviderFactory for it
			if (_providerName != null)
				_factory = SqlClientFactory.Instance;

			// Resolve the DB Type
			string DBTypeName = (_factory == null ? _sharedConnection.GetType() : _factory.GetType()).Name;
			_dbType = DatabaseType.Resolve(DBTypeName, _providerName);

			// What character is used for delimiting parameters in SQL
			_paramPrefix = _dbType.GetParameterPrefix(_connectionString);
		}

		#endregion

		#region IDisposable
		/// <summary>
		/// Automatically close one open shared connection 
		/// </summary>
		public void Dispose()
		{
			// Automatically close one open connection reference
			//  (Works with KeepConnectionAlive and manually opening a shared connection)
			CloseSharedConnection();
		}
		#endregion

		#region Connection Management
		/// <summary>
		/// When set to true the first opened connection is kept alive until this object is disposed
		/// </summary>
		public bool KeepConnectionAlive 
		{ 
			get; 
			set; 
		}

		/// <summary>
		/// Open a connection that will be used for all subsequent queries.
		/// </summary>
		/// <remarks>
		/// Calls to Open/CloseSharedConnection are reference counted and should be balanced
		/// </remarks>
		public void OpenSharedConnection()
		{
			if (_sharedConnectionDepth == 0)
			{
				_sharedConnection = new SqlConnection();
				_sharedConnection.ConnectionString = _connectionString;

				if (_sharedConnection.State == ConnectionState.Broken)
					_sharedConnection.Close();

				if (_sharedConnection.State == ConnectionState.Closed)
					_sharedConnection.Open();

				_sharedConnection = OnConnectionOpened(_sharedConnection);

				if (KeepConnectionAlive)
					_sharedConnectionDepth++;		// Make sure you call Dispose
			}
			_sharedConnectionDepth++;
		}

		/// <summary>
		/// Releases the shared connection
		/// </summary>
		public void CloseSharedConnection()
		{
			if (_sharedConnectionDepth > 0)
			{
				_sharedConnectionDepth--;
				if (_sharedConnectionDepth == 0)
				{
					OnConnectionClosing(_sharedConnection);
					_sharedConnection.Dispose();
					_sharedConnection = null;
				}
			}
		}

		/// <summary>
		/// Provides access to the currently open shared connection (or null if none)
		/// </summary>
		public SqlConnection Connection
		{
			get { return _sharedConnection; }
		}

		#endregion

		#region Transaction Management
		// Helper to create a transaction scope

		/// <summary>
		/// Starts or continues a transaction.
		/// </summary>
		/// <returns>An ITransaction reference that must be Completed or disposed</returns>
		/// <remarks>
		/// This method makes management of calls to Begin/End/CompleteTransaction easier.  
		/// 
		/// The usage pattern for this should be:
		/// 
		/// using (var tx = db.GetTransaction())
		/// {
		///		// Do stuff
		///		db.Update(...);
		///		
		///     // Mark the transaction as complete
		///     tx.Complete();
		/// }
		/// 
		/// Transactions can be nested but they must all be completed otherwise the entire
		/// transaction is aborted.
		/// </remarks>
		public ITransaction GetTransaction()
		{
			return new Transaction(this);
		}

		/// <summary>
		/// Called when a transaction starts.  Overridden by the T4 template generated database
		/// classes to ensure the same DB instance is used throughout the transaction.
		/// </summary>
		public virtual void OnBeginTransaction() 
		{ 
		}

		/// <summary>
		/// Called when a transaction ends.
		/// </summary>
		public virtual void OnEndTransaction() 
		{ 
		}

		/// <summary>
		/// Starts a transaction scope, see GetTransaction() for recommended usage
		/// </summary>
		public void BeginTransaction()
		{
			_transactionDepth++;

			if (_transactionDepth == 1)
			{
				OpenSharedConnection();
				_transaction = _sharedConnection.BeginTransaction();
				_transactionCancelled = false;
				OnBeginTransaction();
			}

		}

		/// <summary>
		/// Internal helper to cleanup transaction
		/// </summary>
		void CleanupTransaction()
		{
			OnEndTransaction();

			if (_transactionCancelled)
				_transaction.Rollback();
			else
				_transaction.Commit();

			_transaction.Dispose();
			_transaction = null;

			CloseSharedConnection();
		}

		/// <summary>
		/// Aborts the entire outer most transaction scope 
		/// </summary>
		/// <remarks>
		/// Called automatically by Transaction.Dispose()
		/// if the transaction wasn't completed.
		/// </remarks>
		public void AbortTransaction()
		{
			_transactionCancelled = true;
			if ((--_transactionDepth) == 0)
				CleanupTransaction();
		}

		/// <summary>
		/// Marks the current transaction scope as complete.
		/// </summary>
		public void CompleteTransaction()
		{
			if ((--_transactionDepth) == 0)
				CleanupTransaction();
		}

		#endregion

		#region Command Management
		/// <summary>
		/// Add a parameter to a DB command
		/// </summary>
		/// <param name="cmd">A reference to the SqlCommand to which the parameter is to be added</param>
		/// <param name="value">The value to assign to the parameter</param>
		/// <param name="pi">Optional, a reference to the property info of the POCO property from which the value is coming.</param>
		void AddParam(SqlCommand cmd, object value, PropertyInfo pi)
		{
			// Convert value to from poco type to db type
			if (pi != null)
			{
				var mapper = Mappers.GetMapper(pi.DeclaringType);
				var fn = mapper.GetToDbConverter(pi);
				if (fn != null)
					value = fn(value);
			}

			// Support passed in parameters
			var idbParam = value as IDbDataParameter;
			if (idbParam != null)
			{
				idbParam.ParameterName = string.Format("{0}{1}", _paramPrefix, cmd.Parameters.Count);
				cmd.Parameters.Add(idbParam);
				return;
			}

			// Create the parameter
			var p = cmd.CreateParameter();
			p.ParameterName = string.Format("{0}{1}", _paramPrefix, cmd.Parameters.Count);

			// Assign the parmeter value
			if (value == null)
			{
				p.Value = DBNull.Value;
			}
			else
			{
				// Give the database type first crack at converting to DB required type
				value = _dbType.MapParameterValue(value);

				var t = value.GetType();
				if (t.IsEnum)		// PostgreSQL .NET driver wont cast enum to int
				{
					p.Value = (int)value;
				}
				else if (t == typeof(Guid))
				{
					p.Value = value.ToString();
					p.DbType = DbType.String;
					p.Size = 40;
				}
				else if (t == typeof(string))
				{
					// out of memory exception occurs if trying to save more than 4000 characters to SQL Server CE NText column. Set before attempting to set Size, or Size will always max out at 4000
					if ((value as string).Length + 1 > 4000 && p.GetType().Name == "SqlCeParameter")
						p.GetType().GetProperty("SqlDbType").SetValue(p, SqlDbType.NText, null); 

					p.Size = Math.Max((value as string).Length + 1, 4000);		// Help query plan caching by using common size
					p.Value = value;
				}
				else if (t == typeof(AnsiString))
				{
					// Thanks @DataChomp for pointing out the SQL Server indexing performance hit of using wrong string type on varchar
					p.Size = Math.Max((value as AnsiString).Value.Length + 1, 4000);
					p.Value = (value as AnsiString).Value;
					p.DbType = DbType.AnsiString;
				}
				else if (value.GetType().Name == "SqlGeography") //SqlGeography is a CLR Type
				{
					p.GetType().GetProperty("UdtTypeName").SetValue(p, "geography", null); //geography is the equivalent SQL Server Type
					p.Value = value;
				}

				else if (value.GetType().Name == "SqlGeometry") //SqlGeometry is a CLR Type
				{
					p.GetType().GetProperty("UdtTypeName").SetValue(p, "geometry", null); //geography is the equivalent SQL Server Type
					p.Value = value;
				}
				else
				{
					p.Value = value;
				}
			}

			// Add to the collection
			cmd.Parameters.Add(p);
		}

		// Create a command
		static Regex rxParamsPrefix = new Regex(@"(?<!@)@\w+", RegexOptions.Compiled);
		public SqlCommand CreateCommand(SqlConnection connection, string sql, params object[] args)
		{
			// Perform named argument replacements
			if (EnableNamedParams)
			{
				var new_args = new List<object>();
				sql = ParametersHelper.ProcessParams(sql, args, new_args);
				args = new_args.ToArray();
			}

			// Perform parameter prefix replacements
			if (_paramPrefix != "@")
				sql = rxParamsPrefix.Replace(sql, m => _paramPrefix + m.Value.Substring(1));
			sql = sql.Replace("@@", "@");		   // <- double @@ escapes a single @

			// Create the command and add parameters
			SqlCommand cmd = connection.CreateCommand();
            cmd.Connection = connection;
			cmd.CommandText = sql;
			cmd.Transaction = _transaction;
			foreach (var item in args)
			{
				AddParam(cmd, item, null);
			}

			// Notify the DB type
			_dbType.PreExecute(cmd);

			// Call logging
			if (!String.IsNullOrEmpty(sql))
				DoPreExecute(cmd);

			return cmd;
		}
		#endregion

		#region Exception Reporting and Logging

		/// <summary>
		/// Called if an exception occurs during processing of a DB operation.  Override to provide custom logging/handling.
		/// </summary>
		/// <param name="x">The exception instance</param>
		/// <returns>True to re-throw the exception, false to suppress it</returns>
		public virtual bool OnException(Exception x)
		{
			System.Diagnostics.Debug.WriteLine(x.ToString());
			System.Diagnostics.Debug.WriteLine(LastCommand);
			return true;
		}

		/// <summary>
		/// Called when DB connection opened
		/// </summary>
		/// <param name="conn">The newly opened SqlConnection</param>
		/// <returns>The same or a replacement SqlConnection</returns>
		/// <remarks>
		/// Override this method to provide custom logging of opening connection, or
		/// to provide a proxy SqlConnection.
		/// </remarks>
		public virtual SqlConnection OnConnectionOpened(SqlConnection conn) 
		{ 
			return conn; 
		}

		/// <summary>
		/// Called when DB connection closed
		/// </summary>
		/// <param name="conn">The soon to be closed IDBConnection</param>
		public virtual void OnConnectionClosing(SqlConnection conn) 
		{ 
		}

		/// <summary>
		/// Called just before an DB command is executed
		/// </summary>
		/// <param name="cmd">The command to be executed</param>
		/// <remarks>
		/// Override this method to provide custom logging of commands and/or
		/// modification of the SqlCommand before it's executed
		/// </remarks>
		public virtual void OnExecutingCommand(SqlCommand cmd) 
		{ 
		}

		/// <summary>
		/// Called on completion of command execution
		/// </summary>
		/// <param name="cmd">The SqlCommand that finished executing</param>
		public virtual void OnExecutedCommand(SqlCommand cmd) 
		{ 
		}

		#endregion

		#region operation: Execute 
		/// <summary>
		/// Executes a non-query command
		/// </summary>
		/// <param name="sql">The SQL statement to execute</param>
		/// <param name="args">Arguments to any embedded parameters in the SQL</param>
		/// <returns>The number of rows affected</returns>
		public async Task<int> Execute(string sql, params object[] args)
		{
			try
			{
				OpenSharedConnection();
				try
				{
					using (var cmd = CreateCommand(_sharedConnection, sql, args))
					{
						var retv=await cmd.ExecuteNonQueryAsync();
						OnExecutedCommand(cmd);
						return retv;
					}
				}
				finally
				{
					CloseSharedConnection();
				}
			}
			catch (Exception x)
			{
				if (OnException(x))
					throw;
				return -1;
			}
		}

		/// <summary>
		/// Executes a non-query command
		/// </summary>
		/// <param name="sql">An SQL builder object representing the query and it's arguments</param>
		/// <returns>The number of rows affected</returns>
		public async Task<int> Execute(Sql sql)
		{
			return await Execute(sql.SQL, sql.Arguments);
		}

		#endregion

		#region operation: ExecuteScalar

		/// <summary>
		/// Executes a query and return the first column of the first row in the result set.
		/// </summary>
		/// <typeparam name="T">The type that the result value should be cast to</typeparam>
		/// <param name="sql">The SQL query to execute</param>
		/// <param name="args">Arguments to any embedded parameters in the SQL</param>
		/// <returns>The scalar value cast to T</returns>
		public async Task<T> ExecuteScalar<T>(string sql, params object[] args)
		{
			try
			{
				OpenSharedConnection();
				try
				{
					using (var cmd = CreateCommand(_sharedConnection, sql, args))
					{
						object val = await cmd.ExecuteScalarAsync();
						OnExecutedCommand(cmd);

						// Handle nullable types
						Type u = Nullable.GetUnderlyingType(typeof(T));
						if (u != null && val == null) 
							return default(T);

						return (T)Convert.ChangeType(val, u==null ? typeof(T) : u);
					}
				}
				finally
				{
					CloseSharedConnection();
				}
			}
			catch (Exception x)
			{
				if (OnException(x))
					throw;
				return default(T);
			}
		}

		/// <summary>
		/// Executes a query and return the first column of the first row in the result set.
		/// </summary>
		/// <typeparam name="T">The type that the result value should be cast to</typeparam>
		/// <param name="sql">An SQL builder object representing the query and it's arguments</param>
		/// <returns>The scalar value cast to T</returns>
        public async Task<T> ExecuteScalar<T>(Sql sql)
		{
			return await ExecuteScalar<T>(sql.SQL, sql.Arguments);
		}

		#endregion

		#region operation: Fetch

		/// <summary>
		/// Runs a query and returns the result set as a typed list
		/// </summary>
		/// <typeparam name="T">The Type representing a row in the result set</typeparam>
		/// <param name="sql">The SQL query to execute</param>
		/// <param name="args">Arguments to any embedded parameters in the SQL</param>
		/// <returns>A List holding the results of the query</returns>
		public async Task<List<T>> Fetch<T>(string sql, params object[] args) 
		{
			var result = await Query<T>(sql, args);
            return result.ToList();
		}

		/// <summary>
		/// Runs a query and returns the result set as a typed list
		/// </summary>
		/// <typeparam name="T">The Type representing a row in the result set</typeparam>
		/// <param name="sql">An SQL builder object representing the query and it's arguments</param>
		/// <returns>A List holding the results of the query</returns>
		public async Task<List<T>> Fetch<T>(Sql sql) 
		{
            var result = await Fetch<T>(sql.SQL, sql.Arguments);
            return result.ToList();
        }

		#endregion

		#region operation: Page

		/// <summary>
		/// Starting with a regular SELECT statement, derives the SQL statements required to query a 
		/// DB for a page of records and the total number of records
		/// </summary>
		/// <typeparam name="T">The Type representing a row in the result set</typeparam>
		/// <param name="skip">The number of rows to skip before the start of the page</param>
		/// <param name="take">The number of rows in the page</param>
		/// <param name="sql">The original SQL select statement</param>
		/// <param name="args">Arguments to any embedded parameters in the SQL</param>
		/// <param name="sqlCount">Outputs the SQL statement to query for the total number of matching rows</param>
		/// <param name="sqlPage">Outputs the SQL statement to retrieve a single page of matching rows</param>
		void BuildPageQueries<T>(long skip, long take, string sql, ref object[] args, out string sqlCount, out string sqlPage) 
		{
			// Add auto select clause
			if (EnableAutoSelect)
				sql = AutoSelectHelper.AddSelectClause<T>(_dbType, sql);

			// Split the SQL
			PagingHelper.SQLParts parts;
			if (!PagingHelper.SplitSQL(sql, out parts))
				throw new Exception("Unable to parse SQL statement for paged query");

			sqlPage = _dbType.BuildPageQuery(skip, take, parts, ref args);
			sqlCount = parts.sqlCount;
		}

		/// <summary>
		/// Retrieves a page of records	and the total number of available records
		/// </summary>
		/// <typeparam name="T">The Type representing a row in the result set</typeparam>
		/// <param name="page">The 1 based page number to retrieve</param>
		/// <param name="itemsPerPage">The number of records per page</param>
		/// <param name="sqlCount">The SQL to retrieve the total number of records</param>
		/// <param name="countArgs">Arguments to any embedded parameters in the sqlCount statement</param>
		/// <param name="sqlPage">The SQL To retrieve a single page of results</param>
		/// <param name="pageArgs">Arguments to any embedded parameters in the sqlPage statement</param>
		/// <returns>A Page of results</returns>
		/// <remarks>
		/// This method allows separate SQL statements to be explicitly provided for the two parts of the page query.
		/// The page and itemsPerPage parameters are not used directly and are used simply to populate the returned Page object.
		/// </remarks>
		public async Task<Page<T>> Page<T>(long page, long itemsPerPage, string sqlCount, object[] countArgs, string sqlPage, object[] pageArgs)
		{
			// Save the one-time command time out and use it for both queries
			var saveTimeout = OneTimeCommandTimeout;

			// Setup the paged result
			var result = new Page<T>
			{
				CurrentPage = page,
				ItemsPerPage = itemsPerPage,
				TotalItems = await ExecuteScalar<long>(sqlCount, countArgs)
			};
			result.TotalPages = result.TotalItems / itemsPerPage;

			if ((result.TotalItems % itemsPerPage) != 0)
				result.TotalPages++;

			OneTimeCommandTimeout = saveTimeout;

			// Get the records
			result.Items = await Fetch<T>(sqlPage, pageArgs);

			// Done
			return result;
		}


		/// <summary>
		/// Retrieves a page of records	and the total number of available records
		/// </summary>
		/// <typeparam name="T">The Type representing a row in the result set</typeparam>
		/// <param name="page">The 1 based page number to retrieve</param>
		/// <param name="itemsPerPage">The number of records per page</param>
		/// <param name="sql">The base SQL query</param>
		/// <param name="args">Arguments to any embedded parameters in the SQL statement</param>
		/// <returns>A Page of results</returns>
		/// <remarks>
		/// SqlPetaPocoAsync will automatically modify the supplied SELECT statement to only retrieve the
		/// records for the specified page.  It will also execute a second query to retrieve the
		/// total number of records in the result set.
		/// </remarks>
		public async Task<Page<T>> Page<T>(long page, long itemsPerPage, string sql, params object[] args) 
		{
			string sqlCount, sqlPage;
			BuildPageQueries<T>((page-1)*itemsPerPage, itemsPerPage, sql, ref args, out sqlCount, out sqlPage);
			var result = await Page<T>(page, itemsPerPage, sqlCount, args, sqlPage, args);
            return result;
		}

		/// <summary>
		/// Retrieves a page of records	and the total number of available records
		/// </summary>
		/// <typeparam name="T">The Type representing a row in the result set</typeparam>
		/// <param name="page">The 1 based page number to retrieve</param>
		/// <param name="itemsPerPage">The number of records per page</param>
		/// <param name="sql">An SQL builder object representing the base SQL query and it's arguments</param>
		/// <returns>A Page of results</returns>
		/// <remarks>
		/// SqlPetaPocoAsync will automatically modify the supplied SELECT statement to only retrieve the
		/// records for the specified page.  It will also execute a second query to retrieve the
		/// total number of records in the result set.
		/// </remarks>
		public async Task<Page<T>> Page<T>(long page, long itemsPerPage, Sql sql)
		{
			var result = await Page<T>(page, itemsPerPage, sql.SQL, sql.Arguments);
            return result;
		}

		/// <summary>
		/// Retrieves a page of records	and the total number of available records
		/// </summary>
		/// <typeparam name="T">The Type representing a row in the result set</typeparam>
		/// <param name="page">The 1 based page number to retrieve</param>
		/// <param name="itemsPerPage">The number of records per page</param>
		/// <param name="sqlCount">An SQL builder object representing the SQL to retrieve the total number of records</param>
		/// <param name="sqlPage">An SQL builder object representing the SQL to retrieve a single page of results</param>
		/// <returns>A Page of results</returns>
		/// <remarks>
		/// This method allows separate SQL statements to be explicitly provided for the two parts of the page query.
		/// The page and itemsPerPage parameters are not used directly and are used simply to populate the returned Page object.
		/// </remarks>
        public async Task<Page<T>> Page<T>(long page, long itemsPerPage, Sql sqlCount, Sql sqlPage)
		{
			var result = await Page<T>(page, itemsPerPage, sqlCount.SQL, sqlCount.Arguments, sqlPage.SQL, sqlPage.Arguments);
            return result;
		}

		#endregion

		#region operation: Fetch (page)

		/// <summary>
		/// Retrieves a page of records (without the total count)
		/// </summary>
		/// <typeparam name="T">The Type representing a row in the result set</typeparam>
		/// <param name="page">The 1 based page number to retrieve</param>
		/// <param name="itemsPerPage">The number of records per page</param>
		/// <param name="sql">The base SQL query</param>
		/// <param name="args">Arguments to any embedded parameters in the SQL statement</param>
		/// <returns>A List of results</returns>
		/// <remarks>
		/// SqlPetaPocoAsync will automatically modify the supplied SELECT statement to only retrieve the
		/// records for the specified page.
		/// </remarks>
		public async Task<List<T>> Fetch<T>(long page, long itemsPerPage, string sql, params object[] args)
		{
			var result = await SkipTake<T>((page - 1) * itemsPerPage, itemsPerPage, sql, args);
            return result;
		}

		/// <summary>
		/// Retrieves a page of records (without the total count)
		/// </summary>
		/// <typeparam name="T">The Type representing a row in the result set</typeparam>
		/// <param name="page">The 1 based page number to retrieve</param>
		/// <param name="itemsPerPage">The number of records per page</param>
		/// <param name="sql">An SQL builder object representing the base SQL query and it's arguments</param>
		/// <returns>A List of results</returns>
		/// <remarks>
		/// SqlPetaPocoAsync will automatically modify the supplied SELECT statement to only retrieve the
		/// records for the specified page.
		/// </remarks>
        public async Task<List<T>> Fetch<T>(long page, long itemsPerPage, Sql sql)
		{
			var result = await SkipTake<T>((page - 1) * itemsPerPage, itemsPerPage, sql.SQL, sql.Arguments);
            return result;
		}

		#endregion

		#region operation: SkipTake

		/// <summary>
		/// Retrieves a range of records from result set
		/// </summary>
		/// <typeparam name="T">The Type representing a row in the result set</typeparam>
		/// <param name="skip">The number of rows at the start of the result set to skip over</param>
		/// <param name="take">The number of rows to retrieve</param>
		/// <param name="sql">The base SQL query</param>
		/// <param name="args">Arguments to any embedded parameters in the SQL statement</param>
		/// <returns>A List of results</returns>
		/// <remarks>
		/// SqlPetaPocoAsync will automatically modify the supplied SELECT statement to only retrieve the
		/// records for the specified range.
		/// </remarks>
        public async Task<List<T>> SkipTake<T>(long skip, long take, string sql, params object[] args)
		{
			string sqlCount, sqlPage;
			BuildPageQueries<T>(skip, take, sql, ref args, out sqlCount, out sqlPage);
			var result = await Fetch<T>(sqlPage, args);
            return result;
		}

		/// <summary>
		/// Retrieves a range of records from result set
		/// </summary>
		/// <typeparam name="T">The Type representing a row in the result set</typeparam>
		/// <param name="skip">The number of rows at the start of the result set to skip over</param>
		/// <param name="take">The number of rows to retrieve</param>
		/// <param name="sql">An SQL builder object representing the base SQL query and it's arguments</param>
		/// <returns>A List of results</returns>
		/// <remarks>
		/// SqlPetaPocoAsync will automatically modify the supplied SELECT statement to only retrieve the
		/// records for the specified range.
		/// </remarks>
        public async Task<List<T>> SkipTake<T>(long skip, long take, Sql sql)
		{
			var result = await SkipTake<T>(skip, take, sql.SQL, sql.Arguments);
            return result;
        }
		#endregion

		#region operation: Query

		/// <summary>
		/// Runs an SQL query, returning the results as an IEnumerable collection
		/// </summary>
		/// <typeparam name="T">The Type representing a row in the result set</typeparam>
		/// <param name="sql">The SQL query</param>
		/// <param name="args">Arguments to any embedded parameters in the SQL statement</param>
		/// <returns>An enumerable collection of result records</returns>
		/// <remarks>
		/// For some DB providers, care should be taken to not start a new Query before finishing with
		/// and disposing the previous one. In cases where this is an issue, consider using Fetch which
		/// returns the results as a List rather than an IEnumerable.
		/// </remarks>
		public async Task<IEnumerable<T>> Query<T>(string sql, params object[] args) 
		{
			if (EnableAutoSelect)
				sql = AutoSelectHelper.AddSelectClause<T>(_dbType, sql);

            var resultList = new List<T>();
			OpenSharedConnection();
			try
			{
				using (var cmd = CreateCommand(_sharedConnection, sql, args))
				{
					SqlDataReader r = null;
					var pd = SqlPocoData.ForType(typeof(T));
					try
					{
						r = await cmd.ExecuteReaderAsync();
						OnExecutedCommand(cmd);
					}
					catch (Exception x)
					{
						if (OnException(x))
							throw;
					}
					var factory = pd.GetFactory(cmd.CommandText, _sharedConnection.ConnectionString, 0, r.FieldCount, r) as Func<IDataReader, T>;
					using (r)
					{
						while (true)
						{
							try
							{
								if (!r.Read())
									break;
								T poco = factory(r);
                                resultList.Add(poco);
                            }
							catch (Exception x)
							{
								if (OnException(x))
									throw;
							}
						}
					}
				}
                return resultList;
			}
			finally
			{
				CloseSharedConnection();
			}
		}

		/// <summary>
		/// Runs an SQL query, returning the results as an IEnumerable collection
		/// </summary>
		/// <typeparam name="T">The Type representing a row in the result set</typeparam>
		/// <param name="sql">An SQL builder object representing the base SQL query and it's arguments</param>
		/// <returns>An enumerable collection of result records</returns>
		/// <remarks>
		/// For some DB providers, care should be taken to not start a new Query before finishing with
		/// and disposing the previous one. In cases where this is an issue, consider using Fetch which
		/// returns the results as a List rather than an IEnumerable.
		/// </remarks>
		public async Task<IEnumerable<T>> Query<T>(Sql sql)
		{
			var result = await Query<T>(sql.SQL, sql.Arguments);
            return result;
		}

		#endregion

		#region operation: Exists

		/// <summary>
		/// Checks for the existance of a row matching the specified condition
		/// </summary>
		/// <typeparam name="T">The Type representing the table being queried</typeparam>
		/// <param name="sqlCondition">The SQL expression to be tested for (ie: the WHERE expression)</param>
		/// <param name="args">Arguments to any embedded parameters in the SQL statement</param>
		/// <returns>True if a record matching the condition is found.</returns>
		public async Task<bool> Exists<T>(string sqlCondition, params object[] args)
		{
			var poco = SqlPocoData.ForType(typeof(T)).TableInfo;
			var result = await ExecuteScalar<int>(string.Format(_dbType.GetExistsSql(), poco.TableName, sqlCondition), args) != 0;
            return result;
		}

		/// <summary>
		/// Checks for the existance of a row with the specified primary key value.
		/// </summary>
		/// <typeparam name="T">The Type representing the table being queried</typeparam>
		/// <param name="primaryKey">The primary key value to look for</param>
		/// <returns>True if a record with the specified primary key value exists.</returns>
        public async Task<bool> Exists<T>(object primaryKey)
		{
			var result = await Exists<T>(string.Format("{0}=@0", _dbType.EscapeSqlIdentifier(SqlPocoData.ForType(typeof(T)).TableInfo.PrimaryKey)), primaryKey);
            return result;
		}

		#endregion

		#region operation: linq style (Exists, Single, SingleOrDefault etc...)

		/// <summary>
		/// Returns the record with the specified primary key value
		/// </summary>
		/// <typeparam name="T">The Type representing a row in the result set</typeparam>
		/// <param name="primaryKey">The primary key value of the record to fetch</param>
		/// <returns>The single record matching the specified primary key value</returns>
		/// <remarks>
		/// Throws an exception if there are zero or more than one record with the specified primary key value.
		/// </remarks>
		public async Task<T> Single<T>(object primaryKey) 
		{
			var result = await Single<T>(string.Format("WHERE {0}=@0", _dbType.EscapeSqlIdentifier(SqlPocoData.ForType(typeof(T)).TableInfo.PrimaryKey)), primaryKey);
            return result;
		}

		/// <summary>
		/// Returns the record with the specified primary key value, or the default value if not found
		/// </summary>
		/// <typeparam name="T">The Type representing a row in the result set</typeparam>
		/// <param name="primaryKey">The primary key value of the record to fetch</param>
		/// <returns>The single record matching the specified primary key value</returns>
		/// <remarks>
		/// If there are no records with the specified primary key value, default(T) (typically null) is returned.
		/// </remarks>
        public async Task<T> SingleOrDefault<T>(object primaryKey) 
		{
            var result = await SingleOrDefault<T>(string.Format("WHERE {0}=@0", _dbType.EscapeSqlIdentifier(SqlPocoData.ForType(typeof(T)).TableInfo.PrimaryKey)), primaryKey);
            return result;
        }

		/// <summary>
		/// Runs a query that should always return a single row.
		/// </summary>
		/// <typeparam name="T">The Type representing a row in the result set</typeparam>
		/// <param name="sql">The SQL query</param>
		/// <param name="args">Arguments to any embedded parameters in the SQL statement</param>
		/// <returns>The single record matching the specified primary key value</returns>
		/// <remarks>
		/// Throws an exception if there are zero or more than one matching record
		/// </remarks>
        public async Task<T> Single<T>(string sql, params object[] args) 
		{
            var result = await Query<T>(sql, args);
            return result.Single();
        }

		/// <summary>
		/// Runs a query that should always return either a single row, or no rows
		/// </summary>
		/// <typeparam name="T">The Type representing a row in the result set</typeparam>
		/// <param name="sql">The SQL query</param>
		/// <param name="args">Arguments to any embedded parameters in the SQL statement</param>
		/// <returns>The single record matching the specified primary key value, or default(T) if no matching rows</returns>
        public async Task<T> SingleOrDefault<T>(string sql, params object[] args) 
		{
			var result = await Query<T>(sql, args);
            return result.SingleOrDefault();
        }

		/// <summary>
		/// Runs a query that should always return at least one return
		/// </summary>
		/// <typeparam name="T">The Type representing a row in the result set</typeparam>
		/// <param name="sql">The SQL query</param>
		/// <param name="args">Arguments to any embedded parameters in the SQL statement</param>
		/// <returns>The first record in the result set</returns>
        public async Task<T> First<T>(string sql, params object[] args) 
		{
            var result = await Query<T>(sql, args);
            return result.First();
        }

		/// <summary>
		/// Runs a query and returns the first record, or the default value if no matching records
		/// </summary>
		/// <typeparam name="T">The Type representing a row in the result set</typeparam>
		/// <param name="sql">The SQL query</param>
		/// <param name="args">Arguments to any embedded parameters in the SQL statement</param>
		/// <returns>The first record in the result set, or default(T) if no matching rows</returns>
        public async Task<T> FirstOrDefault<T>(string sql, params object[] args) 
		{
            var result = await Query<T>(sql, args);
            return result.FirstOrDefault();
        }


		/// <summary>
		/// Runs a query that should always return a single row.
		/// </summary>
		/// <typeparam name="T">The Type representing a row in the result set</typeparam>
		/// <param name="sql">An SQL builder object representing the query and it's arguments</param>
		/// <returns>The single record matching the specified primary key value</returns>
		/// <remarks>
		/// Throws an exception if there are zero or more than one matching record
		/// </remarks>
        public async Task<T> Single<T>(Sql sql) 
		{
            var result = await Query<T>(sql);
            return result.Single();
        }

		/// <summary>
		/// Runs a query that should always return either a single row, or no rows
		/// </summary>
		/// <typeparam name="T">The Type representing a row in the result set</typeparam>
		/// <param name="sql">An SQL builder object representing the query and it's arguments</param>
		/// <returns>The single record matching the specified primary key value, or default(T) if no matching rows</returns>
        public async Task<T> SingleOrDefault<T>(Sql sql) 
		{
            var result = await Query<T>(sql);
            return result.SingleOrDefault();
        }

		/// <summary>
		/// Runs a query that should always return at least one return
		/// </summary>
		/// <typeparam name="T">The Type representing a row in the result set</typeparam>
		/// <param name="sql">An SQL builder object representing the query and it's arguments</param>
		/// <returns>The first record in the result set</returns>
        public async Task<T> First<T>(Sql sql) 
		{
            var result = await Query<T>(sql);
            return result.First();
        }

		/// <summary>
		/// Runs a query and returns the first record, or the default value if no matching records
		/// </summary>
		/// <typeparam name="T">The Type representing a row in the result set</typeparam>
		/// <param name="sql">An SQL builder object representing the query and it's arguments</param>
		/// <returns>The first record in the result set, or default(T) if no matching rows</returns>
        public async Task<T> FirstOrDefault<T>(Sql sql) 
		{
            var result = await Query<T>(sql);
            return result.FirstOrDefault();
        }
		#endregion

		#region operation: Insert

		/// <summary>
		/// Performs an SQL Insert
		/// </summary>
		/// <param name="tableName">The name of the table to insert into</param>
		/// <param name="primaryKeyName">The name of the primary key column of the table</param>
		/// <param name="poco">The POCO object that specifies the column values to be inserted</param>
		/// <returns>The auto allocated primary key of the new record</returns>
		public async Task<object> Insert(string tableName, string primaryKeyName, object poco)
		{
			var result = Insert(tableName, primaryKeyName, true, poco);
            return result;
		}



		/// <summary>
		/// Performs an SQL Insert
		/// </summary>
		/// <param name="tableName">The name of the table to insert into</param>
		/// <param name="primaryKeyName">The name of the primary key column of the table</param>
		/// <param name="autoIncrement">True if the primary key is automatically allocated by the DB</param>
		/// <param name="poco">The POCO object that specifies the column values to be inserted</param>
		/// <returns>The auto allocated primary key of the new record, or null for non-auto-increment tables</returns>
		/// <remarks>Inserts a poco into a table.  If the poco has a property with the same name 
		/// as the primary key the id of the new record is assigned to it.  Either way,
		/// the new id is returned.</remarks>
        public async Task<object> Insert(string tableName, string primaryKeyName, bool autoIncrement, object poco)
		{
			try
			{
				OpenSharedConnection();
				try
				{
					using (var cmd = CreateCommand(_sharedConnection, ""))
					{
						var pd = SqlPocoData.ForObject(poco, primaryKeyName);
						var names = new List<string>();
						var values = new List<string>();
						var index = 0;
						foreach (var i in pd.Columns)
						{
							// Don't insert result columns
							if (i.Value.ResultColumn)
								continue;

							// Don't insert the primary key (except under oracle where we need bring in the next sequence value)
							if (autoIncrement && primaryKeyName != null && string.Compare(i.Key, primaryKeyName, true)==0)
							{
								// Setup auto increment expression
								string autoIncExpression = _dbType.GetAutoIncrementExpression(pd.TableInfo);
								if (autoIncExpression!=null)
								{
									names.Add(i.Key);
									values.Add(autoIncExpression);
								}
								continue;
							}

							names.Add(_dbType.EscapeSqlIdentifier(i.Key));
							values.Add(string.Format("{0}{1}", _paramPrefix, index++));
							AddParam(cmd, i.Value.GetValue(poco), i.Value.PropertyInfo);
						}

						string outputClause = String.Empty;
						if (autoIncrement)
						{
							outputClause = _dbType.GetInsertOutputClause(primaryKeyName);
						}


						cmd.CommandText = string.Format("INSERT INTO {0} ({1}){2} VALUES ({3})",
								_dbType.EscapeTableName(tableName),
								string.Join(",", names.ToArray()),
								outputClause,
								string.Join(",", values.ToArray())
								);

						if (!autoIncrement)
						{
							DoPreExecute(cmd);
							await cmd.ExecuteNonQueryAsync();
							OnExecutedCommand(cmd);

							PocoColumn pkColumn;
							if (primaryKeyName != null && pd.Columns.TryGetValue(primaryKeyName, out pkColumn))
								return pkColumn.GetValue(poco);
							else
								return null;
						}


						object id = await _dbType.ExecuteInsert(this, cmd, primaryKeyName);


						// Assign the ID back to the primary key property
						if (primaryKeyName != null)
						{
							PocoColumn pc;
							if (pd.Columns.TryGetValue(primaryKeyName, out pc))
							{
								pc.SetValue(poco, pc.ChangeType(id));
							}
						}

						return id;
					}
				}
				finally
				{
					CloseSharedConnection();
				}
			}
			catch (Exception x)
			{
				if (OnException(x))
					throw;
				return null;
			}
		}

		/// <summary>
		/// Performs an SQL Insert
		/// </summary>
		/// <param name="poco">The POCO object that specifies the column values to be inserted</param>
		/// <returns>The auto allocated primary key of the new record, or null for non-auto-increment tables</returns>
		/// <remarks>The name of the table, it's primary key and whether it's an auto-allocated primary key are retrieved
		/// from the POCO's attributes</remarks>
		public async Task<object> Insert(object poco)
		{
			var pd = SqlPocoData.ForType(poco.GetType());
			var result = await Insert(pd.TableInfo.TableName, pd.TableInfo.PrimaryKey, pd.TableInfo.AutoIncrement, poco);
            return result;
		}

		#endregion

		#region operation: Update

		/// <summary>
		/// Performs an SQL update
		/// </summary>
		/// <param name="tableName">The name of the table to update</param>
		/// <param name="primaryKeyName">The name of the primary key column of the table</param>
		/// <param name="poco">The POCO object that specifies the column values to be updated</param>
		/// <param name="primaryKeyValue">The primary key of the record to be updated</param>
		/// <returns>The number of affected records</returns>
        public async Task<int> Update(string tableName, string primaryKeyName, object poco, object primaryKeyValue)
		{
			var result = await Update(tableName, primaryKeyName, poco, primaryKeyValue, null);
            return result;
		}

		/// <summary>
		/// Performs an SQL update
		/// </summary>
		/// <param name="tableName">The name of the table to update</param>
		/// <param name="primaryKeyName">The name of the primary key column of the table</param>
		/// <param name="poco">The POCO object that specifies the column values to be updated</param>
		/// <param name="primaryKeyValue">The primary key of the record to be updated</param>
		/// <param name="columns">The column names of the columns to be updated, or null for all</param>
		/// <returns>The number of affected rows</returns>
        public async Task<int> Update(string tableName, string primaryKeyName, object poco, object primaryKeyValue, IEnumerable<string> columns)
		{
			try
			{
				OpenSharedConnection();
				try
				{
					using (var cmd = CreateCommand(_sharedConnection, ""))
					{
						var sb = new StringBuilder();
						var index = 0;
						var pd = SqlPocoData.ForObject(poco,primaryKeyName);
						if (columns == null)
						{
							foreach (var i in pd.Columns)
							{
								// Don't update the primary key, but grab the value if we don't have it
								if (string.Compare(i.Key, primaryKeyName, true) == 0)
								{
									if (primaryKeyValue == null)
										primaryKeyValue = i.Value.GetValue(poco);
									continue;
								}

								// Dont update result only columns
								if (i.Value.ResultColumn)
									continue;

								// Build the sql
								if (index > 0)
									sb.Append(", ");
								sb.AppendFormat("{0} = {1}{2}", _dbType.EscapeSqlIdentifier(i.Key), _paramPrefix, index++);

								// Store the parameter in the command
								AddParam(cmd, i.Value.GetValue(poco), i.Value.PropertyInfo);
							}
						}
						else
						{
							foreach (var colname in columns)
							{
								var pc = pd.Columns[colname];

								// Build the sql
								if (index > 0)
									sb.Append(", ");
								sb.AppendFormat("{0} = {1}{2}", _dbType.EscapeSqlIdentifier(colname), _paramPrefix, index++);

								// Store the parameter in the command
								AddParam(cmd, pc.GetValue(poco), pc.PropertyInfo);
							}

							// Grab primary key value
							if (primaryKeyValue == null)
							{
								var pc = pd.Columns[primaryKeyName];
								primaryKeyValue = pc.GetValue(poco);
							}

						}

						// Find the property info for the primary key
						PropertyInfo pkpi=null;
						if (primaryKeyName != null)
						{
							pkpi = pd.Columns[primaryKeyName].PropertyInfo;
						}

						cmd.CommandText = string.Format("UPDATE {0} SET {1} WHERE {2} = {3}{4}",
											_dbType.EscapeTableName(tableName), sb.ToString(), _dbType.EscapeSqlIdentifier(primaryKeyName), _paramPrefix, index++);
						AddParam(cmd, primaryKeyValue, pkpi);

						DoPreExecute(cmd);

						// Do it
						var retv= await cmd.ExecuteNonQueryAsync();
						OnExecutedCommand(cmd);
						return retv;
					}
				}
				finally
				{
					CloseSharedConnection();
				}
			}
			catch (Exception x)
			{
				if (OnException(x))
					throw;
				return -1;
			}
		}

		/// <summary>
		/// Performs an SQL update
		/// </summary>
		/// <param name="tableName">The name of the table to update</param>
		/// <param name="primaryKeyName">The name of the primary key column of the table</param>
		/// <param name="poco">The POCO object that specifies the column values to be updated</param>
		/// <returns>The number of affected rows</returns>
		public async Task<int> Update(string tableName, string primaryKeyName, object poco)
		{
			var result = await Update(tableName, primaryKeyName, poco, null);
            return result;
		}

		/// <summary>
		/// Performs an SQL update
		/// </summary>
		/// <param name="tableName">The name of the table to update</param>
		/// <param name="primaryKeyName">The name of the primary key column of the table</param>
		/// <param name="poco">The POCO object that specifies the column values to be updated</param>
		/// <param name="columns">The column names of the columns to be updated, or null for all</param>
		/// <returns>The number of affected rows</returns>
        public async Task<int> Update(string tableName, string primaryKeyName, object poco, IEnumerable<string> columns)
		{
            var result = await Update(tableName, primaryKeyName, poco, null, columns);
            return result;

		}

		/// <summary>
		/// Performs an SQL update
		/// </summary>
		/// <param name="poco">The POCO object that specifies the column values to be updated</param>
		/// <param name="columns">The column names of the columns to be updated, or null for all</param>
		/// <returns>The number of affected rows</returns>
        public async Task<int> Update(object poco, IEnumerable<string> columns)
		{
            var result = await Update(poco, null, columns);
            return result;
        }

		/// <summary>
		/// Performs an SQL update
		/// </summary>
		/// <param name="poco">The POCO object that specifies the column values to be updated</param>
		/// <returns>The number of affected rows</returns>
        public async Task<int> Update(object poco)
		{
            var result = await Update(poco, null, null);
            return result;
        }

		/// <summary>
		/// Performs an SQL update
		/// </summary>
		/// <param name="poco">The POCO object that specifies the column values to be updated</param>
		/// <param name="primaryKeyValue">The primary key of the record to be updated</param>
		/// <returns>The number of affected rows</returns>
        public async Task<int> Update(object poco, object primaryKeyValue)
		{
            var result = await Update(poco, primaryKeyValue, null);
            return result;
        }

		/// <summary>
		/// Performs an SQL update
		/// </summary>
		/// <param name="poco">The POCO object that specifies the column values to be updated</param>
		/// <param name="primaryKeyValue">The primary key of the record to be updated</param>
		/// <param name="columns">The column names of the columns to be updated, or null for all</param>
		/// <returns>The number of affected rows</returns>
        public async Task<int> Update(object poco, object primaryKeyValue, IEnumerable<string> columns)
		{
			var pd = SqlPocoData.ForType(poco.GetType());
            var result = await Update(pd.TableInfo.TableName, pd.TableInfo.PrimaryKey, poco, primaryKeyValue, columns);
            return result;
        }

		/// <summary>
		/// Performs an SQL update
		/// </summary>
		/// <typeparam name="T">The POCO class who's attributes specify the name of the table to update</typeparam>
		/// <param name="sql">The SQL update and condition clause (ie: everything after "UPDATE tablename"</param>
		/// <param name="args">Arguments to any embedded parameters in the SQL</param>
		/// <returns>The number of affected rows</returns>
        public async Task<int> Update<T>(string sql, params object[] args)
		{
			var pd = SqlPocoData.ForType(typeof(T));
            var result = await Execute(string.Format("UPDATE {0} {1}", _dbType.EscapeTableName(pd.TableInfo.TableName), sql), args);
            return result;
        }

		/// <summary>
		/// Performs an SQL update
		/// </summary>
		/// <typeparam name="T">The POCO class who's attributes specify the name of the table to update</typeparam>
		/// <param name="sql">An SQL builder object representing the SQL update and condition clause (ie: everything after "UPDATE tablename"</param>
		/// <returns>The number of affected rows</returns>
        public async Task<int> Update<T>(Sql sql)
		{
			var pd = SqlPocoData.ForType(typeof(T));
			var result = await Execute(new Sql(string.Format("UPDATE {0}", _dbType.EscapeTableName(pd.TableInfo.TableName))).Append(sql));
            return result;
        }
		#endregion

		#region operation: Delete

		/// <summary>
		/// Performs and SQL Delete
		/// </summary>
		/// <param name="tableName">The name of the table to delete from</param>
		/// <param name="primaryKeyName">The name of the primary key column</param>
		/// <param name="poco">The POCO object whose primary key value will be used to delete the row</param>
		/// <returns>The number of rows affected</returns>
		public async Task<int> Delete(string tableName, string primaryKeyName, object poco)
		{
			var result = await Delete(tableName, primaryKeyName, poco, null);
            return result;
		}

		/// <summary>
		/// Performs and SQL Delete
		/// </summary>
		/// <param name="tableName">The name of the table to delete from</param>
		/// <param name="primaryKeyName">The name of the primary key column</param>
		/// <param name="poco">The POCO object whose primary key value will be used to delete the row (or null to use the supplied primary key value)</param>
		/// <param name="primaryKeyValue">The value of the primary key identifing the record to be deleted (or null, or get this value from the POCO instance)</param>
		/// <returns>The number of rows affected</returns>
		public async Task<int> Delete(string tableName, string primaryKeyName, object poco, object primaryKeyValue)
		{
			// If primary key value not specified, pick it up from the object
			if (primaryKeyValue == null)
			{
				var pd = SqlPocoData.ForObject(poco,primaryKeyName);
				PocoColumn pc;
				if (pd.Columns.TryGetValue(primaryKeyName, out pc))
				{
					primaryKeyValue = pc.GetValue(poco);
				}
			}

			// Do it
			var sql = string.Format("DELETE FROM {0} WHERE {1}=@0", _dbType.EscapeTableName(tableName), _dbType.EscapeSqlIdentifier(primaryKeyName));
			var result = await Execute(sql, primaryKeyValue);
            return result;
		}

		/// <summary>
		/// Performs an SQL Delete
		/// </summary>
		/// <param name="poco">The POCO object specifying the table name and primary key value of the row to be deleted</param>
		/// <returns>The number of rows affected</returns>
		public async Task<int> Delete(object poco)
		{
			var pd = SqlPocoData.ForType(poco.GetType());
			var result = await Delete(pd.TableInfo.TableName, pd.TableInfo.PrimaryKey, poco);
            return result;

		}

		/// <summary>
		/// Performs an SQL Delete
		/// </summary>
		/// <typeparam name="T">The POCO class whose attributes identify the table and primary key to be used in the delete</typeparam>
		/// <param name="pocoOrPrimaryKey">The value of the primary key of the row to delete</param>
		/// <returns></returns>
        public async Task<int> Delete<T>(object pocoOrPrimaryKey)
		{
            if (pocoOrPrimaryKey.GetType() == typeof(T))
            {
                var result = await Delete(pocoOrPrimaryKey);
                return result;
            }
            else
            {
                var pd = SqlPocoData.ForType(typeof(T));
                var result = await Delete(pd.TableInfo.TableName, pd.TableInfo.PrimaryKey, null, pocoOrPrimaryKey);
                return result;
            }
        }

		/// <summary>
		/// Performs an SQL Delete
		/// </summary>
		/// <typeparam name="T">The POCO class who's attributes specify the name of the table to delete from</typeparam>
		/// <param name="sql">The SQL condition clause identifying the row to delete (ie: everything after "DELETE FROM tablename"</param>
		/// <param name="args">Arguments to any embedded parameters in the SQL</param>
		/// <returns>The number of affected rows</returns>
        public async Task<int> Delete<T>(string sql, params object[] args)
		{
			var pd = SqlPocoData.ForType(typeof(T));
            var result = await Execute(string.Format("DELETE FROM {0} {1}", _dbType.EscapeTableName(pd.TableInfo.TableName), sql), args);
            return result;
        }

		/// <summary>
		/// Performs an SQL Delete
		/// </summary>
		/// <typeparam name="T">The POCO class who's attributes specify the name of the table to delete from</typeparam>
		/// <param name="sql">An SQL builder object representing the SQL condition clause identifying the row to delete (ie: everything after "UPDATE tablename"</param>
		/// <returns>The number of affected rows</returns>
        public async Task<int> Delete<T>(Sql sql)
		{
			var pd = SqlPocoData.ForType(typeof(T));
			var result = await Execute(new Sql(string.Format("DELETE FROM {0}", _dbType.EscapeTableName(pd.TableInfo.TableName))).Append(sql));
            return result;
        }
		#endregion

		#region operation: IsNew

		/// <summary>
		/// Check if a poco represents a new row
		/// </summary>
		/// <param name="primaryKeyName">The name of the primary key column</param>
		/// <param name="poco">The object instance whose "newness" is to be tested</param>
		/// <returns>True if the POCO represents a record already in the database</returns>
		/// <remarks>This method simply tests if the POCO's primary key column property has been set to something non-zero.</remarks>
		public bool IsNew(string primaryKeyName, object poco)
		{
			var pd = SqlPocoData.ForObject(poco, primaryKeyName);
			object pk;
			PocoColumn pc;
			if (pd.Columns.TryGetValue(primaryKeyName, out pc))
			{
				pk = pc.GetValue(poco);
			}
#if !PETAPOCO_NO_DYNAMIC
			else if (poco.GetType() == typeof(System.Dynamic.ExpandoObject))
			{
				return true;
			}
#endif
			else
			{
				var pi = poco.GetType().GetProperty(primaryKeyName);
				if (pi == null)
					throw new ArgumentException(string.Format("The object doesn't have a property matching the primary key column name '{0}'", primaryKeyName));
				pk = pi.GetValue(poco, null);
			}

			if (pk == null)
				return true;

			var type = pk.GetType();

			if (type.IsValueType)
			{
				// Common primary key types
				if (type == typeof(long))
					return (long)pk == default(long);
				else if (type == typeof(ulong))
					return (ulong)pk == default(ulong);
				else if (type == typeof(int))
					return (int)pk == default(int);
				else if (type == typeof(uint))
					return (uint)pk == default(uint);
				else if (type == typeof(Guid))
					return (Guid)pk == default(Guid);

				// Create a default instance and compare
				return pk == Activator.CreateInstance(pk.GetType());
			}
			else
			{
				return pk == null;
			}
		}

		/// <summary>
		/// Check if a poco represents a new row
		/// </summary>
		/// <param name="poco">The object instance whose "newness" is to be tested</param>
		/// <returns>True if the POCO represents a record already in the database</returns>
		/// <remarks>This method simply tests if the POCO's primary key column property has been set to something non-zero.</remarks>
		public bool IsNew(object poco)
		{
			var pd = SqlPocoData.ForType(poco.GetType());
			if (!pd.TableInfo.AutoIncrement)
				throw new InvalidOperationException("IsNew() and Save() are only supported on tables with auto-increment/identity primary key columns");
			return IsNew(pd.TableInfo.PrimaryKey, poco);
		}
		#endregion

		#region operation: Save
		/// <summary>
		/// Saves a POCO by either performing either an SQL Insert or SQL Update
		/// </summary>
		/// <param name="tableName">The name of the table to be updated</param>
		/// <param name="primaryKeyName">The name of the primary key column</param>
		/// <param name="poco">The POCO object to be saved</param>
		public async Task Save(string tableName, string primaryKeyName, object poco)
		{
			if (IsNew(primaryKeyName, poco))
			{
				await Insert(tableName, primaryKeyName, true, poco);
			}
			else
			{
                await Update(tableName, primaryKeyName, poco);
			}
		}

		/// <summary>
		/// Saves a POCO by either performing either an SQL Insert or SQL Update
		/// </summary>
		/// <param name="poco">The POCO object to be saved</param>
		public async Task Save(object poco)
		{
			var pd = SqlPocoData.ForType(poco.GetType());
			await Save(pd.TableInfo.TableName, pd.TableInfo.PrimaryKey, poco);
		}
		#endregion

		#region operation: Multi-Poco Query/Fetch
		/// <summary>
		/// Perform a multi-poco fetch
		/// </summary>
		/// <typeparam name="T1">The first POCO type</typeparam>
		/// <typeparam name="T2">The second POCO type</typeparam>
		/// <typeparam name="TRet">The returned list POCO type</typeparam>
		/// <param name="cb">A callback function to connect the POCO instances, or null to automatically guess the relationships</param>
		/// <param name="sql">The SQL query to be executed</param>
		/// <param name="args">Arguments to any embedded parameters in the SQL</param>
		/// <returns>A collection of POCO's as a List</returns>
        public async Task<List<TRet>> Fetch<T1, T2, TRet>(Func<T1, T2, TRet> cb, string sql, params object[] args) 
        {
            var result = await Query<T1, T2, TRet>(cb, sql, args);
            return result.ToList(); 
        }

		/// <summary>
		/// Perform a multi-poco fetch
		/// </summary>
		/// <typeparam name="T1">The first POCO type</typeparam>
		/// <typeparam name="T2">The second POCO type</typeparam>
		/// <typeparam name="T3">The third POCO type</typeparam>
		/// <typeparam name="TRet">The returned list POCO type</typeparam>
		/// <param name="cb">A callback function to connect the POCO instances, or null to automatically guess the relationships</param>
		/// <param name="sql">The SQL query to be executed</param>
		/// <param name="args">Arguments to any embedded parameters in the SQL</param>
		/// <returns>A collection of POCO's as a List</returns>
		public async Task<List<TRet>> Fetch<T1, T2, T3, TRet>(Func<T1, T2, T3, TRet> cb, string sql, params object[] args) 
        {
            var result = await Query<T1, T2, T3, TRet>(cb, sql, args);
            return result.ToList();
        }

		/// <summary>
		/// Perform a multi-poco fetch
		/// </summary>
		/// <typeparam name="T1">The first POCO type</typeparam>
		/// <typeparam name="T2">The second POCO type</typeparam>
		/// <typeparam name="T3">The third POCO type</typeparam>
		/// <typeparam name="T4">The fourth POCO type</typeparam>
		/// <typeparam name="TRet">The returned list POCO type</typeparam>
		/// <param name="cb">A callback function to connect the POCO instances, or null to automatically guess the relationships</param>
		/// <param name="sql">The SQL query to be executed</param>
		/// <param name="args">Arguments to any embedded parameters in the SQL</param>
		/// <returns>A collection of POCO's as a List</returns>
        public async Task<List<TRet>> Fetch<T1, T2, T3, T4, TRet>(Func<T1, T2, T3, T4, TRet> cb, string sql, params object[] args) 
        {
            var result = await Query<T1, T2, T3, T4, TRet>(cb, sql, args);
            return result.ToList();
        }

		/// <summary>
		/// Perform a multi-poco query
		/// </summary>
		/// <typeparam name="T1">The first POCO type</typeparam>
		/// <typeparam name="T2">The second POCO type</typeparam>
		/// <typeparam name="TRet">The type of objects in the returned IEnumerable</typeparam>
		/// <param name="cb">A callback function to connect the POCO instances, or null to automatically guess the relationships</param>
		/// <param name="sql">The SQL query to be executed</param>
		/// <param name="args">Arguments to any embedded parameters in the SQL</param>
		/// <returns>A collection of POCO's as an IEnumerable</returns>
        public async Task<IEnumerable<TRet>> Query<T1, T2, TRet>(Func<T1, T2, TRet> cb, string sql, params object[] args) 
        {
            var result = await Query<TRet>(new Type[] { typeof(T1), typeof(T2) }, cb, sql, args);
            return result;
        }

		/// <summary>
		/// Perform a multi-poco query
		/// </summary>
		/// <typeparam name="T1">The first POCO type</typeparam>
		/// <typeparam name="T2">The second POCO type</typeparam>
		/// <typeparam name="T3">The third POCO type</typeparam>
		/// <typeparam name="TRet">The type of objects in the returned IEnumerable</typeparam>
		/// <param name="cb">A callback function to connect the POCO instances, or null to automatically guess the relationships</param>
		/// <param name="sql">The SQL query to be executed</param>
		/// <param name="args">Arguments to any embedded parameters in the SQL</param>
		/// <returns>A collection of POCO's as an IEnumerable</returns>
        public async Task<IEnumerable<TRet>> Query<T1, T2, T3, TRet>(Func<T1, T2, T3, TRet> cb, string sql, params object[] args)
        { 
            var result = await Query<TRet>(new Type[] { typeof(T1), typeof(T2), typeof(T3) }, cb, sql, args);
            return result;
        }

		/// <summary>
		/// Perform a multi-poco query
		/// </summary>
		/// <typeparam name="T1">The first POCO type</typeparam>
		/// <typeparam name="T2">The second POCO type</typeparam>
		/// <typeparam name="T3">The third POCO type</typeparam>
		/// <typeparam name="T4">The fourth POCO type</typeparam>
		/// <typeparam name="TRet">The type of objects in the returned IEnumerable</typeparam>
		/// <param name="cb">A callback function to connect the POCO instances, or null to automatically guess the relationships</param>
		/// <param name="sql">The SQL query to be executed</param>
		/// <param name="args">Arguments to any embedded parameters in the SQL</param>
		/// <returns>A collection of POCO's as an IEnumerable</returns>
        public async Task<IEnumerable<TRet>> Query<T1, T2, T3, T4, TRet>(Func<T1, T2, T3, T4, TRet> cb, string sql, params object[] args) 
        {
            var result = await Query<TRet>(new Type[] { typeof(T1), typeof(T2), typeof(T3), typeof(T4) }, cb, sql, args);
            return result;
        }

		/// <summary>
		/// Perform a multi-poco fetch
		/// </summary>
		/// <typeparam name="T1">The first POCO type</typeparam>
		/// <typeparam name="T2">The second POCO type</typeparam>
		/// <typeparam name="TRet">The returned list POCO type</typeparam>
		/// <param name="cb">A callback function to connect the POCO instances, or null to automatically guess the relationships</param>
		/// <param name="sql">An SQL builder object representing the query and it's arguments</param>
		/// <returns>A collection of POCO's as a List</returns>
        public async Task<List<TRet>> Fetch<T1, T2, TRet>(Func<T1, T2, TRet> cb, Sql sql) 
        {
            var result = await Query<T1, T2, TRet>(cb, sql.SQL, sql.Arguments);
            return result.ToList();
        }

		/// <summary>
		/// Perform a multi-poco fetch
		/// </summary>
		/// <typeparam name="T1">The first POCO type</typeparam>
		/// <typeparam name="T2">The second POCO type</typeparam>
		/// <typeparam name="T3">The third POCO type</typeparam>
		/// <typeparam name="TRet">The returned list POCO type</typeparam>
		/// <param name="cb">A callback function to connect the POCO instances, or null to automatically guess the relationships</param>
		/// <param name="sql">An SQL builder object representing the query and it's arguments</param>
		/// <returns>A collection of POCO's as a List</returns>
        public async Task<List<TRet>> Fetch<T1, T2, T3, TRet>(Func<T1, T2, T3, TRet> cb, Sql sql) 
        {
            var result = await Query<T1, T2, T3, TRet>(cb, sql.SQL, sql.Arguments);
            return result.ToList();
        }

		/// <summary>
		/// Perform a multi-poco fetch
		/// </summary>
		/// <typeparam name="T1">The first POCO type</typeparam>
		/// <typeparam name="T2">The second POCO type</typeparam>
		/// <typeparam name="T3">The third POCO type</typeparam>
		/// <typeparam name="T4">The fourth POCO type</typeparam>
		/// <typeparam name="TRet">The returned list POCO type</typeparam>
		/// <param name="cb">A callback function to connect the POCO instances, or null to automatically guess the relationships</param>
		/// <param name="sql">An SQL builder object representing the query and it's arguments</param>
		/// <returns>A collection of POCO's as a List</returns>
        public async Task<List<TRet>> Fetch<T1, T2, T3, T4, TRet>(Func<T1, T2, T3, T4, TRet> cb, Sql sql) 
        {
            var result = await Query<T1, T2, T3, T4, TRet>(cb, sql.SQL, sql.Arguments);
            return result.ToList();
        }

		/// <summary>
		/// Perform a multi-poco query
		/// </summary>
		/// <typeparam name="T1">The first POCO type</typeparam>
		/// <typeparam name="T2">The second POCO type</typeparam>
		/// <typeparam name="TRet">The type of objects in the returned IEnumerable</typeparam>
		/// <param name="cb">A callback function to connect the POCO instances, or null to automatically guess the relationships</param>
		/// <param name="sql">An SQL builder object representing the query and it's arguments</param>
		/// <returns>A collection of POCO's as an IEnumerable</returns>
        public async Task<IEnumerable<TRet>> Query<T1, T2, TRet>(Func<T1, T2, TRet> cb, Sql sql) 
        {
            var result = await Query<TRet>(new Type[] { typeof(T1), typeof(T2) }, cb, sql.SQL, sql.Arguments);
            return result;
        }

		/// <summary>
		/// Perform a multi-poco query
		/// </summary>
		/// <typeparam name="T1">The first POCO type</typeparam>
		/// <typeparam name="T2">The second POCO type</typeparam>
		/// <typeparam name="T3">The third POCO type</typeparam>
		/// <typeparam name="TRet">The type of objects in the returned IEnumerable</typeparam>
		/// <param name="cb">A callback function to connect the POCO instances, or null to automatically guess the relationships</param>
		/// <param name="sql">An SQL builder object representing the query and it's arguments</param>
		/// <returns>A collection of POCO's as an IEnumerable</returns>
        public async Task<IEnumerable<TRet>> Query<T1, T2, T3, TRet>(Func<T1, T2, T3, TRet> cb, Sql sql) 
        { 
            var result = await Query<TRet>(new Type[] { typeof(T1), typeof(T2), typeof(T3) }, cb, sql.SQL, sql.Arguments);
            return result;
        }

		/// <summary>
		/// Perform a multi-poco query
		/// </summary>
		/// <typeparam name="T1">The first POCO type</typeparam>
		/// <typeparam name="T2">The second POCO type</typeparam>
		/// <typeparam name="T3">The third POCO type</typeparam>
		/// <typeparam name="T4">The fourth POCO type</typeparam>
		/// <typeparam name="TRet">The type of objects in the returned IEnumerable</typeparam>
		/// <param name="cb">A callback function to connect the POCO instances, or null to automatically guess the relationships</param>
		/// <param name="sql">An SQL builder object representing the query and it's arguments</param>
		/// <returns>A collection of POCO's as an IEnumerable</returns>
        public async Task<IEnumerable<TRet>> Query<T1, T2, T3, T4, TRet>(Func<T1, T2, T3, T4, TRet> cb, Sql sql) 
        {
            var result = await Query<TRet>(new Type[] { typeof(T1), typeof(T2), typeof(T3), typeof(T4) }, cb, sql.SQL, sql.Arguments);
            return result;
        }

		/// <summary>
		/// Perform a multi-poco fetch
		/// </summary>
		/// <typeparam name="T1">The first POCO type</typeparam>
		/// <typeparam name="T2">The second POCO type</typeparam>
		/// <param name="sql">The SQL query to be executed</param>
		/// <param name="args">Arguments to any embedded parameters in the SQL</param>
		/// <returns>A collection of POCO's as a List</returns>
        public async Task<List<T1>> Fetch<T1, T2>(string sql, params object[] args) 
        {
            var result = await Query<T1, T2>(sql, args);
            return result.ToList();
        }

		/// <summary>
		/// Perform a multi-poco fetch
		/// </summary>
		/// <typeparam name="T1">The first POCO type</typeparam>
		/// <typeparam name="T2">The second POCO type</typeparam>
		/// <typeparam name="T3">The third POCO type</typeparam>
		/// <param name="sql">The SQL query to be executed</param>
		/// <param name="args">Arguments to any embedded parameters in the SQL</param>
		/// <returns>A collection of POCO's as a List</returns>
		public async Task<List<T1>> Fetch<T1, T2, T3>(string sql, params object[] args) 
        {
            var result = await Query<T1, T2, T3>(sql, args);
            return result.ToList();
        }

		/// <summary>
		/// Perform a multi-poco fetch
		/// </summary>
		/// <typeparam name="T1">The first POCO type</typeparam>
		/// <typeparam name="T2">The second POCO type</typeparam>
		/// <typeparam name="T3">The third POCO type</typeparam>
		/// <typeparam name="T4">The fourth POCO type</typeparam>
		/// <param name="sql">The SQL query to be executed</param>
		/// <param name="args">Arguments to any embedded parameters in the SQL</param>
		/// <returns>A collection of POCO's as a List</returns>
		public async Task<List<T1>> Fetch<T1, T2, T3, T4>(string sql, params object[] args) 
        {
            var result = await Query<T1, T2, T3, T4>(sql, args);
            return result.ToList();
        }

		/// <summary>
		/// Perform a multi-poco query
		/// </summary>
		/// <typeparam name="T1">The first POCO type</typeparam>
		/// <typeparam name="T2">The second POCO type</typeparam>
		/// <param name="sql">The SQL query to be executed</param>
		/// <param name="args">Arguments to any embedded parameters in the SQL</param>
		/// <returns>A collection of POCO's as an IEnumerable</returns>
        public async Task<IEnumerable<T1>> Query<T1, T2>(string sql, params object[] args) 
        { 
            var result = await Query<T1>(new Type[] { typeof(T1), typeof(T2) }, null, sql, args);
            return result;
        }

		/// <summary>
		/// Perform a multi-poco query
		/// </summary>
		/// <typeparam name="T1">The first POCO type</typeparam>
		/// <typeparam name="T2">The second POCO type</typeparam>
		/// <typeparam name="T3">The third POCO type</typeparam>
		/// <param name="sql">The SQL query to be executed</param>
		/// <param name="args">Arguments to any embedded parameters in the SQL</param>
		/// <returns>A collection of POCO's as an IEnumerable</returns>
        public async Task<IEnumerable<T1>> Query<T1, T2, T3>(string sql, params object[] args) 
        {
            var result = await Query<T1>(new Type[] { typeof(T1), typeof(T2), typeof(T3) }, null, sql, args);
            return result;
        }

		/// <summary>
		/// Perform a multi-poco query
		/// </summary>
		/// <typeparam name="T1">The first POCO type</typeparam>
		/// <typeparam name="T2">The second POCO type</typeparam>
		/// <typeparam name="T3">The third POCO type</typeparam>
		/// <typeparam name="T4">The fourth POCO type</typeparam>
		/// <param name="sql">The SQL query to be executed</param>
		/// <param name="args">Arguments to any embedded parameters in the SQL</param>
		/// <returns>A collection of POCO's as an IEnumerable</returns>
        public async Task<IEnumerable<T1>> Query<T1, T2, T3, T4>(string sql, params object[] args) 
        {
            var result = await Query<T1>(new Type[] { typeof(T1), typeof(T2), typeof(T3), typeof(T4) }, null, sql, args);
            return result;
        }

		/// <summary>
		/// Perform a multi-poco fetch
		/// </summary>
		/// <typeparam name="T1">The first POCO type</typeparam>
		/// <typeparam name="T2">The second POCO type</typeparam>
		/// <param name="sql">An SQL builder object representing the query and it's arguments</param>
		/// <returns>A collection of POCO's as a List</returns>
		public async Task<List<T1>> Fetch<T1, T2>(Sql sql) 
        {
            var result = await Query<T1, T2>(sql.SQL, sql.Arguments);
            return result.ToList();
        }

		/// <summary>
		/// Perform a multi-poco fetch
		/// </summary>
		/// <typeparam name="T1">The first POCO type</typeparam>
		/// <typeparam name="T2">The second POCO type</typeparam>
		/// <typeparam name="T3">The third POCO type</typeparam>
		/// <param name="sql">An SQL builder object representing the query and it's arguments</param>
		/// <returns>A collection of POCO's as a List</returns>
		public async Task<List<T1>> Fetch<T1, T2, T3>(Sql sql) 
        {
            var result = await Query<T1, T2, T3>(sql.SQL, sql.Arguments);
            return result.ToList(); 
        }

		/// <summary>
		/// Perform a multi-poco fetch
		/// </summary>
		/// <typeparam name="T1">The first POCO type</typeparam>
		/// <typeparam name="T2">The second POCO type</typeparam>
		/// <typeparam name="T3">The third POCO type</typeparam>
		/// <typeparam name="T4">The fourth POCO type</typeparam>
		/// <param name="sql">An SQL builder object representing the query and it's arguments</param>
		/// <returns>A collection of POCO's as a List</returns>
		public async Task<List<T1>> Fetch<T1, T2, T3, T4>(Sql sql) 
        {
            var result = await Query<T1, T2, T3, T4>(sql.SQL, sql.Arguments);
            return result.ToList(); 
        }

		/// <summary>
		/// Perform a multi-poco query
		/// </summary>
		/// <typeparam name="T1">The first POCO type</typeparam>
		/// <typeparam name="T2">The second POCO type</typeparam>
		/// <param name="sql">An SQL builder object representing the query and it's arguments</param>
		/// <returns>A collection of POCO's as an IEnumerable</returns>
        public async Task<IEnumerable<T1>> Query<T1, T2>(Sql sql) 
        {
            var result = await Query<T1>(new Type[] { typeof(T1), typeof(T2) }, null, sql.SQL, sql.Arguments);
            return result;
        }

		/// <summary>
		/// Perform a multi-poco query
		/// </summary>
		/// <typeparam name="T1">The first POCO type</typeparam>
		/// <typeparam name="T2">The second POCO type</typeparam>
		/// <typeparam name="T3">The third POCO type</typeparam>
		/// <param name="sql">An SQL builder object representing the query and it's arguments</param>
		/// <returns>A collection of POCO's as an IEnumerable</returns>
        public async Task<IEnumerable<T1>> Query<T1, T2, T3>(Sql sql) 
        {
            var result = await Query<T1>(new Type[] { typeof(T1), typeof(T2), typeof(T3) }, null, sql.SQL, sql.Arguments);
            return result;
        }

		/// <summary>
		/// Perform a multi-poco query
		/// </summary>
		/// <typeparam name="T1">The first POCO type</typeparam>
		/// <typeparam name="T2">The second POCO type</typeparam>
		/// <typeparam name="T3">The third POCO type</typeparam>
		/// <typeparam name="T4">The fourth POCO type</typeparam>
		/// <param name="sql">An SQL builder object representing the query and it's arguments</param>
		/// <returns>A collection of POCO's as an IEnumerable</returns>
		public async Task<IEnumerable<T1>> Query<T1, T2, T3, T4>(Sql sql) 
        {
            var result = await Query<T1>(new Type[] { typeof(T1), typeof(T2), typeof(T3), typeof(T4) }, null, sql.SQL, sql.Arguments);
            return result;
        }

		/// <summary>
		/// Performs a multi-poco query
		/// </summary>
		/// <typeparam name="TRet">The type of objects in the returned IEnumerable</typeparam>
		/// <param name="types">An array of Types representing the POCO types of the returned result set.</param>
		/// <param name="cb">A callback function to connect the POCO instances, or null to automatically guess the relationships</param>
		/// <param name="sql">The SQL query to be executed</param>
		/// <param name="args">Arguments to any embedded parameters in the SQL</param>
		/// <returns>A collection of POCO's as an IEnumerable</returns>
		public async Task<List<TRet>> Query<TRet>(Type[] types, object cb, string sql, params object[] args)
		{
			OpenSharedConnection();
            List<TRet> resultList = new List<TRet>();
			try
			{
				using (var cmd = CreateCommand(_sharedConnection, sql, args))
				{
					SqlDataReader r = null;
					try
					{
						r = await cmd.ExecuteReaderAsync();
						OnExecutedCommand(cmd);
					}
					catch (Exception x)
					{
						if (OnException(x))
							throw;
					}
					var factory = MultiPocoFactory.GetFactory<TRet>(types, _sharedConnection.ConnectionString, sql, r);
					if (cb == null)
						cb = MultiPocoFactory.GetAutoMapper(types.ToArray());
					bool bNeedTerminator = false;
					using (r)
					{
						while (true)
						{
							try
							{
								if (!r.Read())
									break;
                                TRet poco = factory(r, cb);
                                if (poco != null)
                                    resultList.Add(poco);
                                else
                                    bNeedTerminator = true;
                            }
							catch (Exception x)
							{
								if (OnException(x))
									throw;
								break;
							}

						}
						if (bNeedTerminator)
						{
							var poco = (TRet)(cb as Delegate).DynamicInvoke(new object[types.Length]);
							if (poco != null)
                                resultList.Add(poco);
						}
					}
				}
                return resultList;
			}
			finally
			{
				CloseSharedConnection();
			}
		}

		#endregion

		#region Last Command

		/// <summary>
		/// Retrieves the SQL of the last executed statement
		/// </summary>
		public string LastSQL { get { return _lastSql; } }

		/// <summary>
		/// Retrieves the arguments to the last execute statement
		/// </summary>
		public object[] LastArgs { get { return _lastArgs; } }


		/// <summary>
		/// Returns a formatted string describing the last executed SQL statement and it's argument values
		/// </summary>
		public string LastCommand
		{
			get { return FormatCommand(_lastSql, _lastArgs); }
		}
		#endregion

		#region FormatCommand

		/// <summary>
		/// Formats the contents of a DB command for display
		/// </summary>
		/// <param name="cmd"></param>
		/// <returns></returns>
		public string FormatCommand(SqlCommand cmd)
		{
			return FormatCommand(cmd.CommandText, (from IDataParameter parameter in cmd.Parameters select parameter.Value).ToArray());
		}

		/// <summary>
		/// Formats an SQL query and it's arguments for display
		/// </summary>
		/// <param name="sql"></param>
		/// <param name="args"></param>
		/// <returns></returns>
		public string FormatCommand(string sql, object[] args)
		{
			var sb = new StringBuilder();
			if (sql == null)
				return "";
			sb.Append(sql);
			if (args != null && args.Length > 0)
			{
				sb.Append("\n");
				for (int i = 0; i < args.Length; i++)
				{
					sb.AppendFormat("\t -> {0}{1} [{2}] = \"{3}\"\n", _paramPrefix, i, args[i].GetType().Name, args[i]);
				}
				sb.Remove(sb.Length - 1, 1);
			}
			return sb.ToString();
		}
		#endregion

		#region Public Properties

		/*
		public static IMapper Mapper
		{
			get;
			set;
		} */

		/// <summary>
		/// When set to true, SqlPetaPocoAsync will automatically create the "SELECT columns" part of any query that looks like it needs it
		/// </summary>
		public bool EnableAutoSelect 
		{ 
			get; 
			set; 
		}

		/// <summary>
		/// When set to true, parameters can be named ?myparam and populated from properties of the passed in argument values.
		/// </summary>
		public bool EnableNamedParams 
		{ 
			get; 
			set; 
		}

		/// <summary>
		/// Sets the timeout value for all SQL statements.
		/// </summary>
		public int CommandTimeout 
		{ 
			get; 
			set; 
		}

		/// <summary>
		/// Sets the timeout value for the next (and only next) SQL statement
		/// </summary>
		public int OneTimeCommandTimeout 
		{ 
			get; 
			set; 
		}
		#endregion

		#region Member Fields
		// Member variables
		internal DatabaseType _dbType;
		string _connectionString;
        string _providerName = "System.Data.SqlClient";
        SqlClientFactory _factory;
		SqlConnection _sharedConnection;
		SqlTransaction _transaction;
		int _sharedConnectionDepth;
		int _transactionDepth;
		bool _transactionCancelled;
		string _lastSql;
		object[] _lastArgs;
		string _paramPrefix;
		#endregion

		#region Internal operations
        internal async Task ExecuteNonQueryHelper(SqlCommand cmd)
		{
			DoPreExecute(cmd);
			await cmd.ExecuteNonQueryAsync();
			OnExecutedCommand(cmd);
		}

		internal async Task<object> ExecuteScalarHelper(SqlCommand cmd)
		{
			DoPreExecute(cmd);
			object r = await cmd.ExecuteScalarAsync();
			OnExecutedCommand(cmd);
			return r;
		}

		internal void DoPreExecute(SqlCommand cmd)
		{
			// Setup command timeout
			if (OneTimeCommandTimeout != 0)
			{
				cmd.CommandTimeout = OneTimeCommandTimeout;
				OneTimeCommandTimeout = 0;
			}
			else if (CommandTimeout != 0)
			{
				cmd.CommandTimeout = CommandTimeout;
			}

			// Call hook
			OnExecutingCommand(cmd);

			// Save it
			_lastSql = cmd.CommandText;
			_lastArgs = (from IDataParameter parameter in cmd.Parameters select parameter.Value).ToArray();
		}



		#endregion
	}


	/// <summary>
	/// For explicit poco properties, marks the property as a column and optionally 
	/// supplies the DB column name.
	/// </summary>
	[AttributeUsage(AttributeTargets.Property)]
	public class ColumnAttribute : Attribute
	{
		public ColumnAttribute() 
		{
			ForceToUtc = false;
		}

		public ColumnAttribute(string Name) 
		{ 
			this.Name = Name;
			ForceToUtc = false;
		}

		public string Name 
		{ 
			get; 
			set; 
		}

		public bool ForceToUtc
		{
			get;
			set;
		}
	}


	/// <summary>
	/// Poco classes marked with the Explicit attribute require all column properties to 
	/// be marked with the Column attribute
	/// </summary>
	[AttributeUsage(AttributeTargets.Class)]
	public class ExplicitColumnsAttribute : Attribute
	{
	}

	/// <summary>
	/// Use the Ignore attribute on POCO class properties that shouldn't be mapped
	/// by SqlPetaPocoAsync.
	/// </summary>
	[AttributeUsage(AttributeTargets.Property)]
	public class IgnoreAttribute : Attribute
	{
	}


	/// <summary>
	/// Specifies the primary key column of a poco class, whether the column is auto incrementing
	/// and the sequence name for Oracle sequence columns.
	/// </summary>
	[AttributeUsage(AttributeTargets.Class)]
	public class PrimaryKeyAttribute : Attribute
	{
		public PrimaryKeyAttribute(string primaryKey)
		{
			Value = primaryKey;
			autoIncrement = true;
		}

		public string Value 
		{ 
			get; 
			private set; 
		}

		public string sequenceName 
		{ 
			get; 
			set; 
		}

		public bool autoIncrement 
		{ 
			get; 
			set; 
		}
	}



	/// <summary>
	/// Marks a poco property as a result only column that is populated in queries
	/// but not used for updates or inserts.
	/// </summary>
	[AttributeUsage(AttributeTargets.Property)]
	public class ResultColumnAttribute : ColumnAttribute
	{
		public ResultColumnAttribute()
		{
		}

		public ResultColumnAttribute(string name)
			: base(name)
		{
		}
	}


	/// <summary>
	/// Sets the DB table name to be used for a Poco class.
	/// </summary>
	[AttributeUsage(AttributeTargets.Class)]
	public class TableNameAttribute : Attribute
	{
		public TableNameAttribute(string tableName)
		{
			Value = tableName;
		}

		public string Value
		{
			get;
			private set;
		}
	}


	/// <summary>
	/// Wrap strings in an instance of this class to force use of DBType.AnsiString
	/// </summary>
	public class AnsiString
	{
		/// <summary>
		/// Constructs an AnsiString
		/// </summary>
		/// <param name="str">The C# string to be converted to ANSI before being passed to the DB</param>
		public AnsiString(string str)
		{
			Value = str;
		}

		/// <summary>
		/// The string value
		/// </summary>
		public string Value 
		{ 
			get; 
			private set; 
		}
	}


	/// <summary>
	/// Hold information about a column in the database.
	/// </summary>
	/// <remarks>
	/// Typically ColumnInfo is automatically populated from the attributes on a POCO object and it's properties. It can
	/// however also be returned from the IMapper interface to provide your owning bindings between the DB and your POCOs.
	/// </remarks>
	public class ColumnInfo
	{
		/// <summary>
		/// The SQL name of the column
		/// </summary>
		public string ColumnName
		{
			get;
			set;
		}

		/// <summary>
		/// True if this column returns a calculated value from the database and shouldn't be used in Insert and Update operations.
		/// </summary>
		public bool ResultColumn
		{
			get;
			set;
		}

		/// <summary>
		/// True if time and date values returned through this column should be forced to UTC DateTimeKind. (no conversion is applied - the Kind of the DateTime property
		/// is simply set to DateTimeKind.Utc instead of DateTimeKind.Unknown.
		/// </summary>
		public bool ForceToUtc
		{
			get;
			set;
		}

		/// <summary>
		/// Creates and populates a ColumnInfo from the attributes of a POCO property.
		/// </summary>
		/// <param name="pi">The property whose column info is required</param>
		/// <returns>A ColumnInfo instance</returns>
		public static ColumnInfo FromProperty(PropertyInfo pi)
		{
			// Check if declaring poco has [Explicit] attribute
			bool ExplicitColumns = pi.DeclaringType.GetCustomAttributes(typeof(ExplicitColumnsAttribute), true).Length > 0;

			// Check for [Column]/[Ignore] Attributes
			var ColAttrs = pi.GetCustomAttributes(typeof(ColumnAttribute), true);
			if (ExplicitColumns)
			{
				if (ColAttrs.Length == 0)
					return null;
			}
			else
			{
				if (pi.GetCustomAttributes(typeof(IgnoreAttribute), true).Length != 0)
					return null;
			}

			ColumnInfo ci = new ColumnInfo();

			// Read attribute
			if (ColAttrs.Length > 0)
			{
				var colattr = (ColumnAttribute)ColAttrs[0];

				ci.ColumnName = colattr.Name==null ? pi.Name : colattr.Name;
				ci.ForceToUtc = colattr.ForceToUtc;
				if ((colattr as ResultColumnAttribute) != null)
					ci.ResultColumn = true;

			}
			else
			{
				ci.ColumnName = pi.Name;
				ci.ForceToUtc = false;
				ci.ResultColumn = false;
			}

			return ci;



		}
	}

	/// <summary>
	/// IMapper provides a way to hook into SqlPetaPocoAsync's Database to POCO mapping mechanism to either
	/// customize or completely replace it.
	/// </summary>
	/// <remarks>
	/// To use this functionality, instantiate a class that implements IMapper and then pass it to
	/// SqlPetaPocoAsync through the static method Mappers.Register()
	/// </remarks>
	public interface IMapper
	{
		/// <summary>
		/// Get information about the table associated with a POCO class
		/// </summary>
		/// <param name="pocoType"></param>
		/// <returns>A TableInfo instance</returns>
		/// <remarks>
		/// This method must return a valid TableInfo.  
		/// To create a TableInfo from a POCO's attributes, use TableInfo.FromPoco
		/// </remarks>
		TableInfo GetTableInfo(Type pocoType);

		/// <summary>
		/// Get information about the column associated with a property of a POCO
		/// </summary>
		/// <param name="pocoProperty">The PropertyInfo of the property being queried</param>
		/// <returns>A reference to a ColumnInfo instance, or null to ignore this property</returns>
		/// <remarks>
		/// To create a ColumnInfo from a property's attributes, use PropertyInfo.FromProperty
		/// </remarks>
		ColumnInfo GetColumnInfo(PropertyInfo pocoProperty);

		/// <summary>
		/// Supply a function to convert a database value to the correct property value
		/// </summary>
		/// <param name="TargetProperty">The target property</param>
		/// <param name="SourceType">The type of data returned by the DB</param>
		/// <returns>A Func that can do the conversion, or null for no conversion</returns>
		Func<object, object> GetFromDbConverter(PropertyInfo TargetProperty, Type SourceType);

		/// <summary>
		/// Supply a function to convert a property value into a database value
		/// </summary>
		/// <param name="SourceProperty">The property to be converted</param>
		/// <returns>A Func that can do the conversion</returns>
		/// <remarks>
		/// This conversion is only used for converting values from POCO's that are 
		/// being Inserted or Updated.  
		/// Conversion is not available for parameter values passed directly to queries.
		/// </remarks>
		Func<object, object> GetToDbConverter(PropertyInfo SourceProperty);
	}


	/// <summary>
	/// This static manages registation of IMapper instances with SqlPetaPocoAsync
	/// </summary>
	public static class Mappers
	{
		/// <summary>
		/// Registers a mapper for all types in a specific assembly
		/// </summary>
		/// <param name="assembly">The assembly whose types are to be managed by this mapper</param>
		/// <param name="mapper">The IMapper implementation</param>
		public static void Register(Assembly assembly, IMapper mapper)
		{
			RegisterInternal(assembly, mapper);
		}

		/// <summary>
		/// Registers a mapper for a single POCO type
		/// </summary>
		/// <param name="type">The type to be managed by this mapper</param>
		/// <param name="mapper">The IMapper implementation</param>
		public static void Register(Type type, IMapper mapper)
		{
			RegisterInternal(type, mapper);
		}

		/// <summary>
		/// Remove all mappers for all types in a specific assembly
		/// </summary>
		/// <param name="assembly">The assembly whose mappers are to be revoked</param>
		public static void Revoke(Assembly assembly)
		{
			RevokeInternal(assembly);
		}

		/// <summary>
		/// Remove the mapper for a specific type
		/// </summary>
		/// <param name="type">The type whose mapper is to be removed</param>
		public static void Revoke(Type type)
		{
			RevokeInternal(type);
		}

		/// <summary>
		/// Revoke an instance of a mapper
		/// </summary>
		/// <param name="mapper">The IMapper to be revkoed</param>
		public static void Revoke(IMapper mapper)
		{
			_lock.EnterWriteLock();
			try
			{
				foreach (var i in _mappers.Where(kvp => kvp.Value == mapper).ToList())
					_mappers.Remove(i.Key);
			}
			finally
			{
				_lock.ExitWriteLock();
				FlushCaches();
			}
		}

		/// <summary>
		/// Retrieve the IMapper implementation to be used for a specified POCO type
		/// </summary>
		/// <param name="t"></param>
		/// <returns></returns>
		public static IMapper GetMapper(Type t)
		{
			_lock.EnterReadLock();
			try
			{
				IMapper val;
				if (_mappers.TryGetValue(t, out val))
					return val;
				if (_mappers.TryGetValue(t.Assembly, out val))
					return val;

				return Singleton<StandardMapper>.Instance;
			}
			finally
			{
				_lock.ExitReadLock();
			}
		}


		static void RegisterInternal(object typeOrAssembly, IMapper mapper)
		{
			_lock.EnterWriteLock();
			try
			{
				_mappers.Add(typeOrAssembly, mapper);
			}
			finally
			{
				_lock.ExitWriteLock();
				FlushCaches();
			}
		}

		static void RevokeInternal(object typeOrAssembly)
		{
			_lock.EnterWriteLock();
			try
			{
				_mappers.Remove(typeOrAssembly);
			}
			finally
			{
				_lock.ExitWriteLock();
				FlushCaches();
			}
		}

		static void FlushCaches()
		{
			// Whenever a mapper is registered or revoked, we have to assume any generated code is no longer valid.
			// Since this should be a rare occurance, the simplest approach is to simply dump everything and start over.
			MultiPocoFactory.FlushCaches();
			SqlPocoData.FlushCaches();
		}

		static Dictionary<object, IMapper> _mappers = new Dictionary<object,IMapper>();
		static ReaderWriterLockSlim _lock = new ReaderWriterLockSlim();
	}

	/// <summary>
	/// Holds the results of a paged request.
	/// </summary>
	/// <typeparam name="T">The type of Poco in the returned result set</typeparam>
	public class Page<T>
	{
		/// <summary>
		/// The current page number contained in this page of result set 
		/// </summary>
		public long CurrentPage 
		{ 
			get; 
			set; 
		}

		/// <summary>
		/// The total number of pages in the full result set
		/// </summary>
		public long TotalPages 
		{ 
			get; 
			set; 
		}

		/// <summary>
		/// The total number of records in the full result set
		/// </summary>
		public long TotalItems 
		{ 
			get; 
			set; 
		}

		/// <summary>
		/// The number of items per page
		/// </summary>
		public long ItemsPerPage 
		{ 
			get; 
			set; 
		}

		/// <summary>
		/// The actual records on this page
		/// </summary>
		public List<T> Items 
		{ 
			get; 
			set; 
		}

		/// <summary>
		/// User property to hold anything.
		/// </summary>
		public object Context 
		{ 
			get; 
			set; 
		}
	}

	/// <summary>
	/// A simple helper class for build SQL statements
	/// </summary>
	public class Sql
	{
		/// <summary>
		/// Default, empty constructor
		/// </summary>
		public Sql()
		{
		}

		/// <summary>
		/// Construct an SQL statement with the supplied SQL and arguments
		/// </summary>
		/// <param name="sql">The SQL statement or fragment</param>
		/// <param name="args">Arguments to any parameters embedded in the SQL</param>
		public Sql(string sql, params object[] args)
		{
			_sql = sql;
			_args = args;
		}

		/// <summary>
		/// Instantiate a new SQL Builder object.  Weirdly implemented as a property but makes
		/// for more elegantly readble fluent style construction of SQL Statements
		/// eg: db.Query(Sql.Builder.Append(....))
		/// </summary>
		public static Sql Builder
		{
			get { return new Sql(); }
		}

		string _sql;
		object[] _args;
		Sql _rhs;
		string _sqlFinal;
		object[] _argsFinal;

		private void Build()
		{
			// already built?
			if (_sqlFinal != null)
				return;

			// Build it
			var sb = new StringBuilder();
			var args = new List<object>();
			Build(sb, args, null);
			_sqlFinal = sb.ToString();
			_argsFinal = args.ToArray();
		}

		/// <summary>
		/// Returns the final SQL statement represented by this builder
		/// </summary>
		public string SQL
		{
			get
			{
				Build();
				return _sqlFinal;
			}
		}

		/// <summary>
		/// Gets the complete, final set of arguments collected by this builder.
		/// </summary>
		public object[] Arguments
		{
			get
			{
				Build();
				return _argsFinal;
			}
		}

		/// <summary>
		/// Append another SQL builder instance to the right-hand-side of this SQL builder
		/// </summary>
		/// <param name="sql">A reference to another SQL builder instance</param>
		/// <returns>A reference to this builder, allowing for fluent style concatenation</returns>
		public Sql Append(Sql sql)
		{
			if (_rhs != null)
				_rhs.Append(sql);
			else
				_rhs = sql;

			return this;
		}

		/// <summary>
		/// Append an SQL fragement to the right-hand-side of this SQL builder
		/// </summary>
		/// <param name="sql">The SQL statement or fragment</param>
		/// <param name="args">Arguments to any parameters embedded in the SQL</param>
		/// <returns>A reference to this builder, allowing for fluent style concatenation</returns>
		public Sql Append(string sql, params object[] args)
		{
			return Append(new Sql(sql, args));
		}

		static bool Is(Sql sql, string sqltype)
		{
			return sql != null && sql._sql != null && sql._sql.StartsWith(sqltype, StringComparison.InvariantCultureIgnoreCase);
		}

		private void Build(StringBuilder sb, List<object> args, Sql lhs)
		{
			if (!String.IsNullOrEmpty(_sql))
			{
				// Add SQL to the string
				if (sb.Length > 0)
				{
					sb.Append("\n");
				}

				var sql = ParametersHelper.ProcessParams(_sql, _args, args);

				if (Is(lhs, "WHERE ") && Is(this, "WHERE "))
					sql = "AND " + sql.Substring(6);
				if (Is(lhs, "ORDER BY ") && Is(this, "ORDER BY "))
					sql = ", " + sql.Substring(9);

				sb.Append(sql);
			}

			// Now do rhs
			if (_rhs != null)
				_rhs.Build(sb, args, this);
		}

		/// <summary>
		/// Appends an SQL WHERE clause to this SQL builder
		/// </summary>
		/// <param name="sql">The condition of the WHERE clause</param>
		/// <param name="args">Arguments to any parameters embedded in the supplied SQL</param>
		/// <returns>A reference to this builder, allowing for fluent style concatenation</returns>
		public Sql Where(string sql, params object[] args)
		{
			return Append(new Sql("WHERE (" + sql + ")", args));
		}

		/// <summary>
		/// Appends an SQL ORDER BY clause to this SQL builder
		/// </summary>
		/// <param name="columns">A collection of SQL column names to order by</param>
		/// <returns>A reference to this builder, allowing for fluent style concatenation</returns>
		public Sql OrderBy(params object[] columns)
		{
			return Append(new Sql("ORDER BY " + String.Join(", ", (from x in columns select x.ToString()).ToArray())));
		}

		/// <summary>
		/// Appends an SQL SELECT clause to this SQL builder
		/// </summary>
		/// <param name="columns">A collection of SQL column names to select<param>
		/// <returns>A reference to this builder, allowing for fluent style concatenation</returns>
		public Sql Select(params object[] columns)
		{
			return Append(new Sql("SELECT " + String.Join(", ", (from x in columns select x.ToString()).ToArray())));
		}

		/// <summary>
		/// Appends an SQL FROM clause to this SQL builder
		/// </summary>
		/// <param name="tables">A collection of table names to be used in the FROM clause</param>
		/// <returns>A reference to this builder, allowing for fluent style concatenation</returns>
		public Sql From(params object[] tables)
		{
			return Append(new Sql("FROM " + String.Join(", ", (from x in tables select x.ToString()).ToArray())));
		}

		/// <summary>
		/// Appends an SQL GROUP BY clause to this SQL builder
		/// </summary>
		/// <param name="columns">A collection of column names to be grouped by</param>
		/// <returns>A reference to this builder, allowing for fluent style concatenation</returns>
		public Sql GroupBy(params object[] columns)
		{
			return Append(new Sql("GROUP BY " + String.Join(", ", (from x in columns select x.ToString()).ToArray())));
		}

		private SqlJoinClause Join(string JoinType, string table)
		{
			return new SqlJoinClause(Append(new Sql(JoinType + table)));
		}

		/// <summary>
		/// Appends an SQL INNER JOIN clause to this SQL builder
		/// </summary>
		/// <param name="table">The name of the table to join</param>
		/// <returns>A reference an SqlJoinClause through which the join condition can be specified</returns>
		public SqlJoinClause InnerJoin(string table) { return Join("INNER JOIN ", table); }

		/// <summary>
		/// Appends an SQL LEFT JOIN clause to this SQL builder
		/// </summary>
		/// <param name="table">The name of the table to join</param>
		/// <returns>A reference an SqlJoinClause through which the join condition can be specified</returns>
		public SqlJoinClause LeftJoin(string table) { return Join("LEFT JOIN ", table); }

		/// <summary>
		/// The SqlJoinClause is a simple helper class used in the construction of SQL JOIN statements with the SQL builder
		/// </summary>
		public class SqlJoinClause
		{
			private readonly Sql _sql;

			public SqlJoinClause(Sql sql)
			{
				_sql = sql;
			}

			/// <summary>
			/// Appends a SQL ON clause after a JOIN statement
			/// </summary>
			/// <param name="onClause">The ON clause to be appended</param>
			/// <param name="args">Arguments to any parameters embedded in the supplied SQL</param>
			/// <returns>A reference to the parent SQL builder, allowing for fluent style concatenation</returns>
			public Sql On(string onClause, params object[] args)
			{
				return _sql.Append("ON " + onClause, args);
			}
		}
	}


	/// <summary>
	/// StandardMapper is the default implementation of IMapper used by SqlPetaPocoAsync
	/// </summary>
	public class StandardMapper : IMapper
	{
		/// <summary>
		/// Constructs a TableInfo for a POCO by reading its attribute data
		/// </summary>
		/// <param name="pocoType">The POCO Type</param>
		/// <returns></returns>
		public TableInfo GetTableInfo(Type pocoType)
		{
			return TableInfo.FromPoco(pocoType);
		}

		/// <summary>
		/// Constructs a ColumnInfo for a POCO property by reading its attribute data
		/// </summary>
		/// <param name="pocoProperty"></param>
		/// <returns></returns>
		public ColumnInfo GetColumnInfo(PropertyInfo pocoProperty)
		{
			return ColumnInfo.FromProperty(pocoProperty);
		}

		public Func<object, object> GetFromDbConverter(PropertyInfo TargetProperty, Type SourceType)
		{
			return null;
		}

		public Func<object, object> GetToDbConverter(PropertyInfo SourceProperty)
		{
			return null;
		}
	}

	/// <summary>
	/// Use by IMapper to override table bindings for an object
	/// </summary>
	public class TableInfo
	{
		/// <summary>
		/// The database table name
		/// </summary>
		public string TableName 
		{ 
			get; 
			set; 
		}

		/// <summary>
		/// The name of the primary key column of the table
		/// </summary>
		public string PrimaryKey 
		{ 
			get; 
			set; 
		}

		/// <summary>
		/// True if the primary key column is an auto-incrementing
		/// </summary>
		public bool AutoIncrement 
		{ 
			get; 
			set; 
		}

		/// <summary>
		/// The name of the sequence used for auto-incrementing Oracle primary key fields
		/// </summary>
		public string SequenceName 
		{ 
			get; 
			set; 
		}


		/// <summary>
		/// Creates and populates a TableInfo from the attributes of a POCO
		/// </summary>
		/// <param name="t">The POCO type</param>
		/// <returns>A TableInfo instance</returns>
		public static TableInfo FromPoco(Type t)
		{
			TableInfo ti = new TableInfo();

			// Get the table name
			var a = t.GetCustomAttributes(typeof(TableNameAttribute), true);
			ti.TableName = a.Length == 0 ? t.Name : (a[0] as TableNameAttribute).Value;

			// Get the primary key
			a = t.GetCustomAttributes(typeof(PrimaryKeyAttribute), true);
			ti.PrimaryKey = a.Length == 0 ? "ID" : (a[0] as PrimaryKeyAttribute).Value;
			ti.SequenceName = a.Length == 0 ? null : (a[0] as PrimaryKeyAttribute).sequenceName;
			ti.AutoIncrement = a.Length == 0 ? false : (a[0] as PrimaryKeyAttribute).autoIncrement;

			return ti;
		}
	}


	public interface ITransaction : IDisposable
	{
		void Complete();
	}

	/// <summary>
	/// Transaction object helps maintain transaction depth counts
	/// </summary>
	public class Transaction : ITransaction
	{
		public Transaction(Database db)
		{
			_db = db;
			_db.BeginTransaction();
		}

		public void Complete()
		{
			_db.CompleteTransaction();
			_db = null;
		}

		public void Dispose()
		{
			if (_db != null)
				_db.AbortTransaction();
		}

		Database _db;
	}

	namespace Internal
	{
		/// <summary>
		/// Base class for DatabaseType handlers - provides default/common handling for different database engines
		/// </summary>
		abstract class DatabaseType
		{
			/// <summary>
			/// Returns the prefix used to delimit parameters in SQL query strings.
			/// </summary>
			/// <param name="ConnectionString"></param>
			/// <returns></returns>
			public virtual string GetParameterPrefix(string ConnectionString)
			{
				return "@";
			}

			/// <summary>
			/// Converts a supplied C# object value into a value suitable for passing to the database
			/// </summary>
			/// <param name="value">The value to convert</param>
			/// <returns>The converted value</returns>
			public virtual object MapParameterValue(object value)
			{
				// Cast bools to integer
				if (value.GetType() == typeof(bool))
				{
					return ((bool)value) ? 1 : 0;
				}

				// Leave it
				return value;
			}

			/// <summary>
			/// Called immediately before a command is executed, allowing for modification of the SqlCommand before it's passed to the database provider
			/// </summary>
			/// <param name="cmd"></param>
			public virtual void PreExecute(SqlCommand cmd)
			{
			}

			/// <summary>
			/// Builds an SQL query suitable for performing page based queries to the database
			/// </summary>
			/// <param name="skip">The number of rows that should be skipped by the query</param>
			/// <param name="take">The number of rows that should be retruend by the query</param>
			/// <param name="parts">The original SQL query after being parsed into it's component parts</param>
			/// <param name="args">Arguments to any embedded parameters in the SQL query</param>
			/// <returns>The final SQL query that should be executed.</returns>
			public virtual string BuildPageQuery(long skip, long take, PagingHelper.SQLParts parts, ref object[] args)
			{
				var sql = string.Format("{0}\nLIMIT @{1} OFFSET @{2}", parts.sql, args.Length, args.Length + 1);
				args = args.Concat(new object[] { take, skip }).ToArray();
				return sql;
			}

			/// <summary>
			/// Returns an SQL Statement that can check for the existance of a row in the database.
			/// </summary>
			/// <returns></returns>
			public virtual string GetExistsSql()
			{
				return "SELECT COUNT(*) FROM {0} WHERE {1}";
			}

			/// <summary>
			/// Escape a tablename into a suitable format for the associated database provider.
			/// </summary>
			/// <param name="tableName">The name of the table (as specified by the client program, or as attributes on the associated POCO class.</param>
			/// <returns>The escaped table name</returns>
			public virtual string EscapeTableName(string tableName)
			{
				// Assume table names with "dot" are already escaped
				return tableName.IndexOf('.') >= 0 ? tableName : EscapeSqlIdentifier(tableName);
			}

			/// <summary>
			/// Escape and arbitary SQL identifier into a format suitable for the associated database provider
			/// </summary>
			/// <param name="str">The SQL identifier to be escaped</param>
			/// <returns>The escaped identifier</returns>
			public virtual string EscapeSqlIdentifier(string str)
			{
				return string.Format("[{0}]", str);
			}

			/// <summary>
			/// Return an SQL expression that can be used to populate the primary key column of an auto-increment column.
			/// </summary>
			/// <param name="ti">Table info describing the table</param>
			/// <returns>An SQL expressions</returns>
			/// <remarks>See the Oracle database type for an example of how this method is used.</remarks>
			public virtual string GetAutoIncrementExpression(TableInfo ti)
			{
				return null;
			}

			/// <summary>
			/// Returns an SQL expression that can be used to specify the return value of auto incremented columns.
			/// </summary>
			/// <param name="primaryKeyName">The primary key of the row being inserted.</param>
			/// <returns>An expression describing how to return the new primary key value</returns>
			/// <remarks>See the SQLServer database provider for an example of how this method is used.</remarks>
			public virtual string GetInsertOutputClause(string primaryKeyName)
			{
				return string.Empty;
			}

			/// <summary>
			/// Performs an Insert operation
			/// </summary>
			/// <param name="db">The calling Database object</param>
			/// <param name="cmd">The insert command to be executed</param>
			/// <param name="PrimaryKeyName">The primary key of the table being inserted into</param>
			/// <returns>The ID of the newly inserted record</returns>
			public virtual async Task<object> ExecuteInsert(Database db, SqlCommand cmd, string PrimaryKeyName)
			{
				cmd.CommandText += ";\nSELECT @@IDENTITY AS NewID;";
				var result = await db.ExecuteScalarHelper(cmd);
                return result;
			}

			/// <summary>
			/// Look at the type and provider name being used and instantiate a suitable DatabaseType instance.
			/// </summary>
			/// <param name="TypeName"></param>
			/// <param name="ProviderName"></param>
			/// <returns></returns>
			public static DatabaseType Resolve(string TypeName, string ProviderName)
			{
                //if (ProviderName.IndexOf("SQLite", StringComparison.InvariantCultureIgnoreCase) >= 0)
                //    return Singleton<SQLiteDatabaseType>.Instance;
                //if (ProviderName.IndexOf("Firebird", StringComparison.InvariantCultureIgnoreCase) >= 0)
                //    return Singleton<FirebirdDatabaseType>.Instance;

				// Assume SQL Server
				return Singleton<SqlServerDatabaseType>.Instance;
			}

		}

		internal class ExpandoColumn : PocoColumn
		{
			public override void SetValue(object target, object val) { (target as IDictionary<string, object>)[ColumnName] = val; }
			public override object GetValue(object target)
			{
				object val = null;
				(target as IDictionary<string, object>).TryGetValue(ColumnName, out val);
				return val;
			}
			public override object ChangeType(object val) { return val; }
		}


		class MultiPocoFactory
		{
			// Instance data used by the Multipoco factory delegate - essentially a list of the nested poco factories to call
			List<Delegate> _delegates;
			public Delegate GetItem(int index) { return _delegates[index]; }

			// Automagically guess the property relationships between various POCOs and create a delegate that will set them up
			public static object GetAutoMapper(Type[] types)
			{
				// Build a key
				var key = new ArrayKey<Type>(types);

				return AutoMappers.Get(key, () =>
					{
						// Create a method
						var m = new DynamicMethod("petapoco_automapper", types[0], types, true);
						var il = m.GetILGenerator();

						for (int i = 1; i < types.Length; i++)
						{
							bool handled = false;
							for (int j = i - 1; j >= 0; j--)
							{
								// Find the property
								var candidates = from p in types[j].GetProperties() where p.PropertyType == types[i] select p;
								if (candidates.Count() == 0)
									continue;
								if (candidates.Count() > 1)
									throw new InvalidOperationException(string.Format("Can't auto join {0} as {1} has more than one property of type {0}", types[i], types[j]));

								// Generate code
								il.Emit(OpCodes.Ldarg_S, j);
								il.Emit(OpCodes.Ldarg_S, i);
								il.Emit(OpCodes.Callvirt, candidates.First().GetSetMethod(true));
								handled = true;
							}

							if (!handled)
								throw new InvalidOperationException(string.Format("Can't auto join {0}", types[i]));
						}

						il.Emit(OpCodes.Ldarg_0);
						il.Emit(OpCodes.Ret);

						// Cache it
						return m.CreateDelegate(Expression.GetFuncType(types.Concat(types.Take(1)).ToArray()));
					}
				);
			}

			// Find the split point in a result set for two different pocos and return the poco factory for the first
			static Delegate FindSplitPoint(Type typeThis, Type typeNext, string ConnectionString, string sql, IDataReader r, ref int pos)
			{
				// Last?
				if (typeNext == null)
					return SqlPocoData.ForType(typeThis).GetFactory(sql, ConnectionString, pos, r.FieldCount - pos, r);

				// Get PocoData for the two types
				SqlPocoData pdThis = SqlPocoData.ForType(typeThis);
				SqlPocoData pdNext = SqlPocoData.ForType(typeNext);

				// Find split point
				int firstColumn = pos;
				var usedColumns = new Dictionary<string, bool>();
				for (; pos < r.FieldCount; pos++)
				{
					// Split if field name has already been used, or if the field doesn't exist in current poco but does in the next
					string fieldName = r.GetName(pos);
					if (usedColumns.ContainsKey(fieldName) || (!pdThis.Columns.ContainsKey(fieldName) && pdNext.Columns.ContainsKey(fieldName)))
					{
						return pdThis.GetFactory(sql, ConnectionString, firstColumn, pos - firstColumn, r);
					}
					usedColumns.Add(fieldName, true);
				}

				throw new InvalidOperationException(string.Format("Couldn't find split point between {0} and {1}", typeThis, typeNext));
			}

			// Create a multi-poco factory
			static Func<IDataReader, object, TRet> CreateMultiPocoFactory<TRet>(Type[] types, string ConnectionString, string sql, IDataReader r)
			{
				var m = new DynamicMethod("petapoco_multipoco_factory", typeof(TRet), new Type[] { typeof(MultiPocoFactory), typeof(IDataReader), typeof(object) }, typeof(MultiPocoFactory));
				var il = m.GetILGenerator();

				// Load the callback
				il.Emit(OpCodes.Ldarg_2);

				// Call each delegate
				var dels = new List<Delegate>();
				int pos = 0;
				for (int i = 0; i < types.Length; i++)
				{
					// Add to list of delegates to call
					var del = FindSplitPoint(types[i], i + 1 < types.Length ? types[i + 1] : null, ConnectionString, sql, r, ref pos);
					dels.Add(del);

					// Get the delegate
					il.Emit(OpCodes.Ldarg_0);													// callback,this
					il.Emit(OpCodes.Ldc_I4, i);													// callback,this,Index
					il.Emit(OpCodes.Callvirt, typeof(MultiPocoFactory).GetMethod("GetItem"));	// callback,Delegate
					il.Emit(OpCodes.Ldarg_1);													// callback,delegate, datareader

					// Call Invoke
					var tDelInvoke = del.GetType().GetMethod("Invoke");
					il.Emit(OpCodes.Callvirt, tDelInvoke);										// Poco left on stack
				}

				// By now we should have the callback and the N pocos all on the stack.  Call the callback and we're done
				il.Emit(OpCodes.Callvirt, Expression.GetFuncType(types.Concat(new Type[] { typeof(TRet) }).ToArray()).GetMethod("Invoke"));
				il.Emit(OpCodes.Ret);

				// Finish up
				return (Func<IDataReader, object, TRet>)m.CreateDelegate(typeof(Func<IDataReader, object, TRet>), new MultiPocoFactory() { _delegates = dels });
			}

			// Various cached stuff
			static Cache<Tuple<Type, ArrayKey<Type>, string, string>, object> MultiPocoFactories = new Cache<Tuple<Type, ArrayKey<Type>, string, string>, object>();
			static Cache<ArrayKey<Type>, object> AutoMappers = new Cache<ArrayKey<Type>, object>();

			internal static void FlushCaches()
			{
				MultiPocoFactories.Flush();
				AutoMappers.Flush();
			}

			// Get (or create) the multi-poco factory for a query
			public static Func<IDataReader, object, TRet> GetFactory<TRet>(Type[] types, string ConnectionString, string sql, IDataReader r)
			{
				var key = Tuple.Create<Type, ArrayKey<Type>, string, string>(typeof(TRet), new ArrayKey<Type>(types), ConnectionString, sql);

				return (Func<IDataReader, object, TRet>)MultiPocoFactories.Get(key, () =>
					{
						return CreateMultiPocoFactory<TRet>(types, ConnectionString, sql, r);
					}
				);
			}

		}


		internal class PocoColumn
		{
			public string ColumnName;
			public PropertyInfo PropertyInfo;
			public bool ResultColumn;
			public bool ForceToUtc;
			public virtual void SetValue(object target, object val) { PropertyInfo.SetValue(target, val, null); }
			public virtual object GetValue(object target) { return PropertyInfo.GetValue(target, null); }
			public virtual object ChangeType(object val) { return Convert.ChangeType(val, PropertyInfo.PropertyType); }
		}

		class SqlPocoData
		{
			public static SqlPocoData ForObject(object o, string primaryKeyName)
			{
				var t = o.GetType();
#if !PETAPOCO_NO_DYNAMIC
				if (t == typeof(System.Dynamic.ExpandoObject))
				{
					var pd = new SqlPocoData();
					pd.TableInfo = new TableInfo();
					pd.Columns = new Dictionary<string, PocoColumn>(StringComparer.OrdinalIgnoreCase);
					pd.Columns.Add(primaryKeyName, new ExpandoColumn() { ColumnName = primaryKeyName });
					pd.TableInfo.PrimaryKey = primaryKeyName;
					pd.TableInfo.AutoIncrement = true;
					foreach (var col in (o as IDictionary<string, object>).Keys)
					{
						if (col != primaryKeyName)
							pd.Columns.Add(col, new ExpandoColumn() { ColumnName = col });
					}
					return pd;
				}
				else
#endif
					return ForType(t);
			}

			public static SqlPocoData ForType(Type t)
			{
#if !PETAPOCO_NO_DYNAMIC
				if (t == typeof(System.Dynamic.ExpandoObject))
					throw new InvalidOperationException("Can't use dynamic types with this method");
#endif

				return _pocoDatas.Get(t, () => new SqlPocoData(t));
			}

			public SqlPocoData()
			{
			}

			public SqlPocoData(Type t)
			{
				type = t;

				// Get the mapper for this type
				var mapper = Mappers.GetMapper(t);

				// Get the table info
				TableInfo = mapper.GetTableInfo(t);

				// Work out bound properties
				Columns = new Dictionary<string, PocoColumn>(StringComparer.OrdinalIgnoreCase);
				foreach (var pi in t.GetProperties())
				{
					ColumnInfo ci = mapper.GetColumnInfo(pi);
					if (ci == null)
						continue;

					var pc = new PocoColumn();
					pc.PropertyInfo = pi;
					pc.ColumnName = ci.ColumnName;
					pc.ResultColumn = ci.ResultColumn;
					pc.ForceToUtc = ci.ForceToUtc;

					// Store it
					Columns.Add(pc.ColumnName, pc);
				}

				// Build column list for automatic select
				QueryColumns = (from c in Columns where !c.Value.ResultColumn select c.Key).ToArray();

			}

			static bool IsIntegralType(Type t)
			{
				var tc = Type.GetTypeCode(t);
				return tc >= TypeCode.SByte && tc <= TypeCode.UInt64;
			}

			// Create factory function that can convert a IDataReader record into a POCO
			public Delegate GetFactory(string sql, string connString, int firstColumn, int countColumns, IDataReader r)
			{
				// Check cache
				var key = Tuple.Create<string, string, int, int>(sql, connString, firstColumn, countColumns);

				return PocoFactories.Get(key, () =>
					{
					// Create the method
					var m = new DynamicMethod("petapoco_factory_" + PocoFactories.Count.ToString(), type, new Type[] { typeof(IDataReader) }, true);
					var il = m.GetILGenerator();
					var mapper = Mappers.GetMapper(type);

#if !PETAPOCO_NO_DYNAMIC
					if (type == typeof(object))
					{
						// var poco=new T()
						il.Emit(OpCodes.Newobj, typeof(System.Dynamic.ExpandoObject).GetConstructor(Type.EmptyTypes));			// obj

						MethodInfo fnAdd = typeof(IDictionary<string, object>).GetMethod("Add");

						// Enumerate all fields generating a set assignment for the column
						for (int i = firstColumn; i < firstColumn + countColumns; i++)
						{
							var srcType = r.GetFieldType(i);

							il.Emit(OpCodes.Dup);						// obj, obj
							il.Emit(OpCodes.Ldstr, r.GetName(i));		// obj, obj, fieldname

							// Get the converter
							Func<object, object> converter = mapper.GetFromDbConverter((PropertyInfo)null, srcType);

							/*
							if (ForceDateTimesToUtc && converter == null && srcType == typeof(DateTime))
								converter = delegate(object src) { return new DateTime(((DateTime)src).Ticks, DateTimeKind.Utc); };
							*/

							// Setup stack for call to converter
							AddConverterToStack(il, converter);

							// r[i]
							il.Emit(OpCodes.Ldarg_0);					// obj, obj, fieldname, converter?,    rdr
							il.Emit(OpCodes.Ldc_I4, i);					// obj, obj, fieldname, converter?,  rdr,i
							il.Emit(OpCodes.Callvirt, fnGetValue);		// obj, obj, fieldname, converter?,  value

							// Convert DBNull to null
							il.Emit(OpCodes.Dup);						// obj, obj, fieldname, converter?,  value, value
							il.Emit(OpCodes.Isinst, typeof(DBNull));	// obj, obj, fieldname, converter?,  value, (value or null)
							var lblNotNull = il.DefineLabel();
							il.Emit(OpCodes.Brfalse_S, lblNotNull);		// obj, obj, fieldname, converter?,  value
							il.Emit(OpCodes.Pop);						// obj, obj, fieldname, converter?
							if (converter != null)
								il.Emit(OpCodes.Pop);					// obj, obj, fieldname, 
							il.Emit(OpCodes.Ldnull);					// obj, obj, fieldname, null
							if (converter != null)
							{
								var lblReady = il.DefineLabel();
								il.Emit(OpCodes.Br_S, lblReady);
								il.MarkLabel(lblNotNull);
								il.Emit(OpCodes.Callvirt, fnInvoke);
								il.MarkLabel(lblReady);
							}
							else
							{
								il.MarkLabel(lblNotNull);
							}

							il.Emit(OpCodes.Callvirt, fnAdd);
						}
					}
					else
#endif
						if (type.IsValueType || type == typeof(string) || type == typeof(byte[]))
						{
							// Do we need to install a converter?
							var srcType = r.GetFieldType(0);
							var converter = GetConverter(mapper, null, srcType, type);

							// "if (!rdr.IsDBNull(i))"
							il.Emit(OpCodes.Ldarg_0);										// rdr
							il.Emit(OpCodes.Ldc_I4_0);										// rdr,0
							il.Emit(OpCodes.Callvirt, fnIsDBNull);							// bool
							var lblCont = il.DefineLabel();
							il.Emit(OpCodes.Brfalse_S, lblCont);
							il.Emit(OpCodes.Ldnull);										// null
							var lblFin = il.DefineLabel();
							il.Emit(OpCodes.Br_S, lblFin);

							il.MarkLabel(lblCont);

							// Setup stack for call to converter
							AddConverterToStack(il, converter);

							il.Emit(OpCodes.Ldarg_0);										// rdr
							il.Emit(OpCodes.Ldc_I4_0);										// rdr,0
							il.Emit(OpCodes.Callvirt, fnGetValue);							// value

							// Call the converter
							if (converter != null)
								il.Emit(OpCodes.Callvirt, fnInvoke);

							il.MarkLabel(lblFin);
							il.Emit(OpCodes.Unbox_Any, type);								// value converted
						}
						else
						{
							// var poco=new T()
							il.Emit(OpCodes.Newobj, type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[0], null));

							// Enumerate all fields generating a set assignment for the column
							for (int i = firstColumn; i < firstColumn + countColumns; i++)
							{
								// Get the PocoColumn for this db column, ignore if not known
								PocoColumn pc;
								if (!Columns.TryGetValue(r.GetName(i), out pc))
									continue;

								// Get the source type for this column
								var srcType = r.GetFieldType(i);
								var dstType = pc.PropertyInfo.PropertyType;

								// "if (!rdr.IsDBNull(i))"
								il.Emit(OpCodes.Ldarg_0);										// poco,rdr
								il.Emit(OpCodes.Ldc_I4, i);										// poco,rdr,i
								il.Emit(OpCodes.Callvirt, fnIsDBNull);							// poco,bool
								var lblNext = il.DefineLabel();
								il.Emit(OpCodes.Brtrue_S, lblNext);								// poco

								il.Emit(OpCodes.Dup);											// poco,poco

								// Do we need to install a converter?
								var converter = GetConverter(mapper, pc, srcType, dstType);

								// Fast
								bool Handled = false;
								if (converter == null)
								{
									var valuegetter = typeof(IDataRecord).GetMethod("Get" + srcType.Name, new Type[] { typeof(int) });
									if (valuegetter != null
											&& valuegetter.ReturnType == srcType
											&& (valuegetter.ReturnType == dstType || valuegetter.ReturnType == Nullable.GetUnderlyingType(dstType)))
									{
										il.Emit(OpCodes.Ldarg_0);										// *,rdr
										il.Emit(OpCodes.Ldc_I4, i);										// *,rdr,i
										il.Emit(OpCodes.Callvirt, valuegetter);							// *,value

										// Convert to Nullable
										if (Nullable.GetUnderlyingType(dstType) != null)
										{
											il.Emit(OpCodes.Newobj, dstType.GetConstructor(new Type[] { Nullable.GetUnderlyingType(dstType) }));
										}

										il.Emit(OpCodes.Callvirt, pc.PropertyInfo.GetSetMethod(true));		// poco
										Handled = true;
									}
								}

								// Not so fast
								if (!Handled)
								{
									// Setup stack for call to converter
									AddConverterToStack(il, converter);

									// "value = rdr.GetValue(i)"
									il.Emit(OpCodes.Ldarg_0);										// *,rdr
									il.Emit(OpCodes.Ldc_I4, i);										// *,rdr,i
									il.Emit(OpCodes.Callvirt, fnGetValue);							// *,value

									// Call the converter
									if (converter != null)
										il.Emit(OpCodes.Callvirt, fnInvoke);

									// Assign it
									il.Emit(OpCodes.Unbox_Any, pc.PropertyInfo.PropertyType);		// poco,poco,value
									il.Emit(OpCodes.Callvirt, pc.PropertyInfo.GetSetMethod(true));		// poco
								}

								il.MarkLabel(lblNext);
							}

							var fnOnLoaded = RecurseInheritedTypes<MethodInfo>(type, (x) => x.GetMethod("OnLoaded", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[0], null));
							if (fnOnLoaded != null)
							{
								il.Emit(OpCodes.Dup);
								il.Emit(OpCodes.Callvirt, fnOnLoaded);
							}
						}

					il.Emit(OpCodes.Ret);

					// Cache it, return it
					return m.CreateDelegate(Expression.GetFuncType(typeof(IDataReader), type));
					}
				);
			}

			private static void AddConverterToStack(ILGenerator il, Func<object, object> converter)
			{
				if (converter != null)
				{
					// Add the converter
					int converterIndex = _converters.Count;
					_converters.Add(converter);

					// Generate IL to push the converter onto the stack
					il.Emit(OpCodes.Ldsfld, fldConverters);
					il.Emit(OpCodes.Ldc_I4, converterIndex);
					il.Emit(OpCodes.Callvirt, fnListGetItem);					// Converter
				}
			}

			private static Func<object, object> GetConverter(IMapper mapper, PocoColumn pc, Type srcType, Type dstType)
			{
				Func<object, object> converter = null;

				// Get converter from the mapper
				if (pc != null)
				{
					converter = mapper.GetFromDbConverter(pc.PropertyInfo, srcType);
					if (converter != null)
						return converter;
				}

				// Standard DateTime->Utc mapper
				if (pc!=null && pc.ForceToUtc && srcType == typeof(DateTime) && (dstType == typeof(DateTime) || dstType == typeof(DateTime?)))
				{
					return delegate(object src) { return new DateTime(((DateTime)src).Ticks, DateTimeKind.Utc); };
				}

				// Forced type conversion including integral types -> enum
				if (dstType.IsEnum && IsIntegralType(srcType))
				{
					if (srcType != typeof(int))
					{
						return delegate(object src) { return Convert.ChangeType(src, typeof(int), null); };
					}
				}
				else if (!dstType.IsAssignableFrom(srcType))
				{
					if (dstType.IsEnum && srcType == typeof(string))
					{
						return delegate(object src) { return EnumMapper.EnumFromString(dstType, (string)src); };
					}
					else
					{
						return delegate(object src) { return Convert.ChangeType(src, dstType, null); };
					}
				}

				return null;
			}


			static T RecurseInheritedTypes<T>(Type t, Func<Type, T> cb)
			{
				while (t != null)
				{
					T info = cb(t);
					if (info != null)
						return info;
					t = t.BaseType;
				}
				return default(T);
			}


			internal static void FlushCaches()
			{
				_pocoDatas.Flush();
			}

			static Cache<Type, SqlPocoData> _pocoDatas = new Cache<Type, SqlPocoData>();
			static List<Func<object, object>> _converters = new List<Func<object, object>>();
			static MethodInfo fnGetValue = typeof(IDataRecord).GetMethod("GetValue", new Type[] { typeof(int) });
			static MethodInfo fnIsDBNull = typeof(IDataRecord).GetMethod("IsDBNull");
			static FieldInfo fldConverters = typeof(SqlPocoData).GetField("_converters", BindingFlags.Static | BindingFlags.GetField | BindingFlags.NonPublic);
			static MethodInfo fnListGetItem = typeof(List<Func<object, object>>).GetProperty("Item").GetGetMethod();
			static MethodInfo fnInvoke = typeof(Func<object, object>).GetMethod("Invoke");
			public Type type;
			public string[] QueryColumns { get; private set; }
			public TableInfo TableInfo { get; private set; }
			public Dictionary<string, PocoColumn> Columns { get; private set; }
			Cache<Tuple<string, string, int, int>, Delegate> PocoFactories = new Cache<Tuple<string, string, int, int>, Delegate>();
		}


		class ArrayKey<T>
		{
			public ArrayKey(T[] keys)
			{
				// Store the keys
				_keys = keys;

				// Calculate the hashcode
				_hashCode = 17;
				foreach (var k in keys)
				{
					_hashCode = _hashCode * 23 + (k==null ? 0 : k.GetHashCode());
				}
			}

			T[] _keys;
			int _hashCode;

			bool Equals(ArrayKey<T> other)
			{
				if (other == null)
					return false;

				if (other._hashCode != _hashCode)
					return false;

				if (other._keys.Length != _keys.Length)
					return false;

				for (int i = 0; i < _keys.Length; i++)
				{
					if (!object.Equals(_keys[i], other._keys[i]))
						return false;
				}

				return true;
			}

			public override bool Equals(object obj)
			{
				return Equals(obj as ArrayKey<T>);
			}

			public override int GetHashCode()
			{
				return _hashCode;
			}

		}

		static class AutoSelectHelper
		{
			public static string AddSelectClause<T>(DatabaseType DatabaseType, string sql)
			{
				if (sql.StartsWith(";"))
					return sql.Substring(1);

				if (!rxSelect.IsMatch(sql))
				{
					var pd = SqlPocoData.ForType(typeof(T));
					var tableName = DatabaseType.EscapeTableName(pd.TableInfo.TableName);
					string cols = pd.Columns.Count != 0 ? string.Join(", ", (from c in pd.QueryColumns select tableName + "." + DatabaseType.EscapeSqlIdentifier(c)).ToArray()) : "NULL";
					if (!rxFrom.IsMatch(sql))
						sql = string.Format("SELECT {0} FROM {1} {2}", cols, tableName, sql);
					else
						sql = string.Format("SELECT {0} {1}", cols, sql);
				}
				return sql;
			}

			static Regex rxSelect = new Regex(@"\A\s*(SELECT|EXECUTE|CALL)\s", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Multiline);
			static Regex rxFrom = new Regex(@"\A\s*FROM\s", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Multiline);
		}

		class Cache<TKey, TValue>
		{
			Dictionary<TKey, TValue> _map = new Dictionary<TKey, TValue>();
			ReaderWriterLockSlim _lock = new ReaderWriterLockSlim();

			public int Count
			{
				get
				{
					return _map.Count;
				}
			}

			public TValue Get(TKey key, Func<TValue> factory)
			{
				// Check cache
				_lock.EnterReadLock();
				TValue val;
				try
				{
					if (_map.TryGetValue(key, out val))
						return val;
				}
				finally
				{
					_lock.ExitReadLock();
				}


				// Cache it
				_lock.EnterWriteLock();
				try
				{
					// Check again
					if (_map.TryGetValue(key, out val))
						return val;

					// Create it
					val = factory();

					// Store it
					_map.Add(key, val);

					// Done
					return val;
				}
				finally
				{
					_lock.ExitWriteLock();
				}
			}

			public void Flush()
			{
				// Cache it
				_lock.EnterWriteLock();
				try
				{
					_map.Clear();
				}
				finally
				{
					_lock.ExitWriteLock();
				}

			}
		}

		internal static class EnumMapper
		{
			public static object EnumFromString(Type enumType, string value)
			{
				Dictionary<string, object> map = _types.Get(enumType, () =>
				{
					var values = Enum.GetValues(enumType);

					var newmap = new Dictionary<string, object>(values.Length, StringComparer.InvariantCultureIgnoreCase);

					foreach (var v in values)
					{
						newmap.Add(v.ToString(), v);
					}

					return newmap;
				});


				return map[value];
			}

			static Cache<Type, Dictionary<string, object>> _types = new Cache<Type,Dictionary<string,object>>();
		}

		internal static class PagingHelper
		{
			public struct SQLParts
			{
				public string sql;
				public string sqlCount;
				public string sqlSelectRemoved;
				public string sqlOrderBy;
			}

			public static bool SplitSQL(string sql, out SQLParts parts)
			{
				parts.sql = sql;
				parts.sqlSelectRemoved = null;
				parts.sqlCount = null;
				parts.sqlOrderBy = null;

				// Extract the columns from "SELECT <whatever> FROM"
				var m = rxColumns.Match(sql);
				if (!m.Success)
					return false;

				// Save column list and replace with COUNT(*)
				Group g = m.Groups[1];
				parts.sqlSelectRemoved = sql.Substring(g.Index);

				if (rxDistinct.IsMatch(parts.sqlSelectRemoved))
					parts.sqlCount = sql.Substring(0, g.Index) + "COUNT(" + m.Groups[1].ToString().Trim() + ") " + sql.Substring(g.Index + g.Length);
				else
					parts.sqlCount = sql.Substring(0, g.Index) + "COUNT(*) " + sql.Substring(g.Index + g.Length);


				// Look for the last "ORDER BY <whatever>" clause not part of a ROW_NUMBER expression
				m = rxOrderBy.Match(parts.sqlCount);
				if (!m.Success)
				{
					parts.sqlOrderBy = null;
				}
				else
				{
					g = m.Groups[0];
					parts.sqlOrderBy = g.ToString();
					parts.sqlCount = parts.sqlCount.Substring(0, g.Index) + parts.sqlCount.Substring(g.Index + g.Length);
				}

				return true;
			}

			public static Regex rxColumns = new Regex(@"\A\s*SELECT\s+((?:\((?>\((?<depth>)|\)(?<-depth>)|.?)*(?(depth)(?!))\)|.)*?)(?<!,\s+)\bFROM\b", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.Compiled);
			public static Regex rxOrderBy = new Regex(@"\bORDER\s+BY\s+(?!.*?(?:\)|\s+)AS\s)(?:\((?>\((?<depth>)|\)(?<-depth>)|.?)*(?(depth)(?!))\)|[\w\(\)\.])+(?:\s+(?:ASC|DESC))?(?:\s*,\s*(?:\((?>\((?<depth>)|\)(?<-depth>)|.?)*(?(depth)(?!))\)|[\w\(\)\.])+(?:\s+(?:ASC|DESC))?)*", RegexOptions.RightToLeft | RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.Compiled);
			public static Regex rxDistinct = new Regex(@"\ADISTINCT\s", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.Compiled);
		}

		internal static class ParametersHelper
		{
			// Helper to handle named parameters from object properties
			public static string ProcessParams(string sql, object[] args_src, List<object> args_dest)
			{
				return rxParams.Replace(sql, m =>
				{
					string param = m.Value.Substring(1);

					object arg_val;

					int paramIndex;
					if (int.TryParse(param, out paramIndex))
					{
						// Numbered parameter
						if (paramIndex < 0 || paramIndex >= args_src.Length)
							throw new ArgumentOutOfRangeException(string.Format("Parameter '@{0}' specified but only {1} parameters supplied (in `{2}`)", paramIndex, args_src.Length, sql));
						arg_val = args_src[paramIndex];
					}
					else
					{
						// Look for a property on one of the arguments with this name
						bool found = false;
						arg_val = null;
						foreach (var o in args_src)
						{
							var pi = o.GetType().GetProperty(param);
							if (pi != null)
							{
								arg_val = pi.GetValue(o, null);
								found = true;
								break;
							}
						}

						if (!found)
							throw new ArgumentException(string.Format("Parameter '@{0}' specified but none of the passed arguments have a property with this name (in '{1}')", param, sql));
					}

					// Expand collections to parameter lists
					if ((arg_val as System.Collections.IEnumerable) != null &&
						(arg_val as string) == null &&
						(arg_val as byte[]) == null)
					{
						var sb = new StringBuilder();
						foreach (var i in arg_val as System.Collections.IEnumerable)
						{
							sb.Append((sb.Length == 0 ? "@" : ",@") + args_dest.Count.ToString());
							args_dest.Add(i);
						}
						return sb.ToString();
					}
					else
					{
						args_dest.Add(arg_val);
						return "@" + (args_dest.Count - 1).ToString();
					}
				}
				);
			}

			static Regex rxParams = new Regex(@"(?<!@)@\w+", RegexOptions.Compiled);
		}

		static class Singleton<T> where T : new()
		{
			public static T Instance = new T();
		}

	}

	namespace DatabaseTypes
	{

		class SqlServerDatabaseType : DatabaseType
		{
			public override string BuildPageQuery(long skip, long take, PagingHelper.SQLParts parts, ref object[] args)
			{
				parts.sqlSelectRemoved = PagingHelper.rxOrderBy.Replace(parts.sqlSelectRemoved, "", 1);
				if (PagingHelper.rxDistinct.IsMatch(parts.sqlSelectRemoved))
				{
					parts.sqlSelectRemoved = "peta_inner.* FROM (SELECT " + parts.sqlSelectRemoved + ") peta_inner";
				}
				var sqlPage = string.Format("SELECT * FROM (SELECT ROW_NUMBER() OVER ({0}) peta_rn, {1}) peta_paged WHERE peta_rn>@{2} AND peta_rn<=@{3}",
										parts.sqlOrderBy == null ? "ORDER BY (SELECT NULL)" : parts.sqlOrderBy, parts.sqlSelectRemoved, args.Length, args.Length + 1);
				args = args.Concat(new object[] { skip, skip + take }).ToArray();

				return sqlPage;
			}

			public override async Task<object> ExecuteInsert(Database db, SqlCommand cmd, string PrimaryKeyName)
			{
				var result = await db.ExecuteScalarHelper(cmd);
                return result;
			}

			public override string GetExistsSql()
			{
				return "IF EXISTS (SELECT 1 FROM {0} WHERE {1}) SELECT 1 ELSE SELECT 0";
			}

			public override string GetInsertOutputClause(string primaryKeyName)
			{
				return String.Format(" OUTPUT INSERTED.[{0}]", primaryKeyName);
			}
		}

	}
}
