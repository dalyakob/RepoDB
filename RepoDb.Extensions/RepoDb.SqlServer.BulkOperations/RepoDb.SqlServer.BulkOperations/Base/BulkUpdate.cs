﻿using RepoDb.Extensions;
using RepoDb.SqlServer.BulkOperations;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RepoDb
{
    public static partial class SqlConnectionExtension
    {
        #region BulkUpdateInternalBase

        /// <summary>
        /// 
        /// </summary>
        /// <typeparam name="TSqlBulkCopy"></typeparam>
        /// <typeparam name="TSqlBulkCopyOptions"></typeparam>
        /// <typeparam name="TSqlBulkCopyColumnMappingCollection"></typeparam>
        /// <typeparam name="TSqlBulkCopyColumnMapping"></typeparam>
        /// <typeparam name="TSqlTransaction"></typeparam>
        /// <param name="connection"></param>
        /// <param name="tableName"></param>
        /// <param name="reader"></param>
        /// <param name="qualifiers"></param>
        /// <param name="mappings"></param>
        /// <param name="options"></param>
        /// <param name="hints"></param>
        /// <param name="bulkCopyTimeout"></param>
        /// <param name="batchSize"></param>
        /// <param name="usePhysicalPseudoTempTable"></param>
        /// <param name="transaction"></param>
        /// <returns></returns>
        internal static int BulkUpdateInternalBase<TSqlBulkCopy, TSqlBulkCopyOptions, TSqlBulkCopyColumnMappingCollection,
            TSqlBulkCopyColumnMapping, TSqlTransaction>(DbConnection connection,
            string tableName,
            DbDataReader reader,
            IEnumerable<Field> qualifiers = null,
            IEnumerable<BulkInsertMapItem> mappings = null,
            TSqlBulkCopyOptions options = default,
            string hints = null,
            int? bulkCopyTimeout = null,
            int? batchSize = null,
            bool? usePhysicalPseudoTempTable = null,
            TSqlTransaction transaction = null)
            where TSqlBulkCopy : class, IDisposable
            where TSqlBulkCopyOptions : Enum
            where TSqlBulkCopyColumnMappingCollection : class
            where TSqlBulkCopyColumnMapping : class
            where TSqlTransaction : DbTransaction
        {
            // Validate
            if (!reader.HasRows)
            {
                return default;
            }

            // Variables
            var dbSetting = connection.GetDbSetting();
            var hasTransaction = (transaction != null);
            var result = default(int);

            // Check the transaction
            if (transaction == null)
            {
                // Add the transaction if not present
                transaction = (TSqlTransaction)connection.EnsureOpen().BeginTransaction();
            }
            else
            {
                // Validate the objects
                SqlConnectionExtension.ValidateTransactionConnectionObject(connection, transaction);
            }

            // Before Execution Time
            var beforeExecutionTime = DateTime.UtcNow;

            // Must be fixed name so the RepoDb.Core caches will not be bloated
            var tempTableName = string.Concat("_RepoDb_BulkUpdate_", GetTableName(tableName, dbSetting));

            // Add a # prefix if not physical
            if (usePhysicalPseudoTempTable != true)
            {
                tempTableName = string.Concat("#", tempTableName);
            }

            try
            {
                // Get the DB Fields
                var dbFields = DbFieldCache.Get(connection, tableName, transaction, true);

                // Variables needed
                var readerFields = Enumerable.Range(0, reader.FieldCount)
                    .Select((index) => reader.GetName(index));
                var fields = dbFields?.Select(dbField => dbField.AsField());
                var primaryDbField = dbFields?.FirstOrDefault(dbField => dbField.IsPrimary);
                var identityDbField = dbFields?.FirstOrDefault(dbField => dbField.IsIdentity);
                var primaryOrIdentityDbField = (primaryDbField ?? identityDbField);

                // Validate the primary keys
                if (qualifiers?.Any() != true)
                {
                    if (primaryOrIdentityDbField == null)
                    {
                        throw new MissingPrimaryKeyException($"No primary key or identity key found for table '{tableName}'.");
                    }
                    else
                    {
                        qualifiers = new[] { primaryOrIdentityDbField.AsField() };
                    }
                }

                // Filter the fields (based on the data reader)
                if (readerFields?.Any() == true)
                {
                    fields = fields
                        .Where(e =>
                            readerFields.Any(fieldName => string.Equals(fieldName, e.Name, StringComparison.OrdinalIgnoreCase)) == true);
                }

                // Filter the fields (based on the mappings)
                if (mappings?.Any() == true)
                {
                    fields = fields
                        .Where(e =>
                            mappings.Any(m => string.Equals(m.SourceColumn, e.Name, StringComparison.OrdinalIgnoreCase)) == true);
                }

                // Throw an error if there are no fields
                if (fields?.Any() != true)
                {
                    throw new MissingFieldException("There are no field(s) found for this operation.");
                }

                // Create a temporary table
                var sql = GetCreateTemporaryTableSqlText(tableName,
                    tempTableName,
                    fields,
                    dbSetting);
                connection.ExecuteNonQuery(sql, transaction: transaction);

                // Set the options to KeepIdentity if needed
                if (object.Equals(options, default(TSqlBulkCopyOptions)) &&
                    identityDbField?.IsIdentity == true &&
                    qualifiers?.Any(
                        field => string.Equals(field.Name, identityDbField?.Name, StringComparison.OrdinalIgnoreCase)) == true &&
                    fields?.Any(
                        field => string.Equals(field.Name, identityDbField?.Name, StringComparison.OrdinalIgnoreCase)) == true)
                {
                    options = Compiler.GetEnumFunc<TSqlBulkCopyOptions>("KeepIdentity")();
                }

                // Filter the DB Fields
                var filteredDbFields = dbFields?
                    .Where(dbField =>
                        fields?.Any(field => string.Equals(field.Name, dbField.Name, StringComparison.OrdinalIgnoreCase)) == true);

                // Do the bulk insertion first
                BulkInsertInternalBase<TSqlBulkCopy, TSqlBulkCopyOptions, TSqlBulkCopyColumnMappingCollection,
                    TSqlBulkCopyColumnMapping, TSqlTransaction>(connection,
                    tempTableName,
                    reader,
                    filteredDbFields,
                    mappings,
                    options,
                    null,
                    bulkCopyTimeout,
                    batchSize,
                    false,
                    false,
                    transaction);

                // Create the clustered index
                sql = GetCreateTemporaryTableClusteredIndexSqlText(tempTableName,
                    qualifiers,
                    dbSetting);
                connection.ExecuteNonQuery(sql, transaction: transaction);

                // Update the actual update
                sql = GetBulkUpdateSqlText(tableName,
                    tempTableName,
                    fields,
                    qualifiers,
                    primaryDbField?.AsField(),
                    identityDbField?.AsField(),
                    hints,
                    dbSetting);
                result = connection.ExecuteNonQuery(sql, commandTimeout: bulkCopyTimeout, transaction: transaction);

                // Drop the table after used
                sql = GetDropTemporaryTableSqlText(tempTableName, dbSetting);
                connection.ExecuteNonQuery(sql, transaction: transaction);

                // Commit the transaction
                if (hasTransaction == false)
                {
                    transaction?.Commit();
                }
            }
            catch
            {
                // Rollback the transaction
                if (hasTransaction == false)
                {
                    transaction?.Rollback();
                }

                // Throw
                throw;
            }
            finally
            {
                // Dispose the transaction
                if (hasTransaction == false)
                {
                    transaction?.Dispose();
                }
            }

            // Result
            return result;
        }

        /// <summary>
        /// Bulk update an instance of <see cref="DataTable"/> object into the database.
        /// </summary>
        /// <param name="connection">The connection object to be used.</param>
        /// <param name="tableName">The target table for bulk-update operation.</param>
        /// <param name="dataTable">The <see cref="DataTable"/> object to be used in the bulk-update operation.</param>
        /// <param name="qualifiers">The qualifier fields to be used for this bulk-update operation. This is defaulted to the primary key; if not present, then it will use the identity key.</param>
        /// <param name="rowState">The state of the rows to be copied to the destination.</param>
        /// <param name="mappings">The list of the columns to be used for mappings. If this parameter is not set, then all columns will be used for mapping.</param>
        /// <param name="options">The bulk-copy options to be used.</param>
        /// <param name="hints">The table hints to be used.</param>
        /// <param name="bulkCopyTimeout">The timeout in seconds to be used.</param>
        /// <param name="batchSize">The size per batch to be used.</param>
        /// <param name="usePhysicalPseudoTempTable">The flags that signify whether to create a physical pseudo table.</param>
        /// <param name="transaction">The transaction to be used.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> object to be used during the asynchronous operation.</param>
        /// <returns>The number of rows affected by the execution.</returns>
        internal static int BulkUpdateInternalBase<TSqlBulkCopy, TSqlBulkCopyOptions, TSqlBulkCopyColumnMappingCollection,
            TSqlBulkCopyColumnMapping, TSqlTransaction>(DbConnection connection,
            string tableName,
            DataTable dataTable,
            IEnumerable<Field> qualifiers = null,
            DataRowState? rowState = null,
            IEnumerable<BulkInsertMapItem> mappings = null,
            TSqlBulkCopyOptions options = default,
            string hints = null,
            int? bulkCopyTimeout = null,
            int? batchSize = null,
            bool? usePhysicalPseudoTempTable = null,
            TSqlTransaction transaction = null)
            where TSqlBulkCopy : class, IDisposable
            where TSqlBulkCopyOptions : Enum
            where TSqlBulkCopyColumnMappingCollection : class
            where TSqlBulkCopyColumnMapping : class
            where TSqlTransaction : DbTransaction
        {
            // Validate
            if (dataTable?.Rows?.Count <= 0)
            {
                return default;
            }

            // Variables
            var dbSetting = connection.GetDbSetting();
            var hasTransaction = (transaction != null);
            var result = default(int);

            // Check the transaction
            if (transaction == null)
            {
                // Add the transaction if not present
                transaction = (TSqlTransaction)connection.EnsureOpen().BeginTransaction();
            }
            else
            {
                // Validate the objects
                SqlConnectionExtension.ValidateTransactionConnectionObject(connection, transaction);
            }

            // Before Execution Time
            var beforeExecutionTime = DateTime.UtcNow;

            // Must be fixed name so the RepoDb.Core caches will not be bloated
            var tempTableName = string.Concat("_RepoDb_BulkUpdate_", GetTableName(tableName, dbSetting));

            // Add a # prefix if not physical
            if (usePhysicalPseudoTempTable != true)
            {
                tempTableName = string.Concat("#", tempTableName);
            }

            try
            {
                // Get the DB Fields
                var dbFields = DbFieldCache.Get(connection, tableName, transaction, true);

                // Variables needed
                var tableFields = Enumerable.Range(0, dataTable.Columns.Count)
                    .Select((index) => dataTable.Columns[index].ColumnName);
                var fields = dbFields?.Select(dbField => dbField.AsField());
                var primaryDbField = dbFields?.FirstOrDefault(dbField => dbField.IsPrimary);
                var identityDbField = dbFields?.FirstOrDefault(dbField => dbField.IsIdentity);
                var primaryOrIdentityDbField = (primaryDbField ?? identityDbField);

                // Validate the primary keys
                if (qualifiers?.Any() != true)
                {
                    if (primaryOrIdentityDbField == null)
                    {
                        throw new MissingPrimaryKeyException($"No primary key or identity key found for table '{tableName}'.");
                    }
                    else
                    {
                        qualifiers = new[] { primaryOrIdentityDbField.AsField() };
                    }
                }

                // Filter the fields (based on the data table)
                if (tableFields?.Any() == true)
                {
                    fields = fields
                        .Where(e =>
                            tableFields.Any(fieldName => string.Equals(fieldName, e.Name, StringComparison.OrdinalIgnoreCase)) == true);
                }

                // Filter the fields (based on the mappings)
                if (mappings?.Any() == true)
                {
                    fields = fields
                        .Where(e =>
                            mappings.Any(m => string.Equals(m.SourceColumn, e.Name, StringComparison.OrdinalIgnoreCase)) == true);
                }

                // Throw an error if there are no fields
                if (fields?.Any() != true)
                {
                    throw new MissingFieldException("There are no field(s) found for this operation.");
                }

                // Create a temporary table
                var sql = GetCreateTemporaryTableSqlText(tableName,
                    tempTableName,
                    fields,
                    dbSetting);
                connection.ExecuteNonQuery(sql, transaction: transaction);

                // Set the options to KeepIdentity if needed
                if (object.Equals(options, default(TSqlBulkCopyOptions)) &&
                    identityDbField?.IsIdentity == true &&
                    fields?.FirstOrDefault(
                        field => string.Equals(field.Name, identityDbField.Name, StringComparison.OrdinalIgnoreCase)) != null)
                {
                    options = Compiler.GetEnumFunc<TSqlBulkCopyOptions>("KeepIdentity")();
                }

                // Filter the DB Fields
                var filteredDbFields = dbFields?
                    .Where(dbField =>
                        fields?.Any(field => string.Equals(field.Name, dbField.Name, StringComparison.OrdinalIgnoreCase)) == true);

                // Do the bulk insertion first
                BulkInsertInternalBase<TSqlBulkCopy, TSqlBulkCopyOptions, TSqlBulkCopyColumnMappingCollection,
                    TSqlBulkCopyColumnMapping, TSqlTransaction>(connection,
                    tempTableName,
                    dataTable,
                    rowState,
                    filteredDbFields,
                    mappings,
                    options,
                    null,
                    bulkCopyTimeout,
                    batchSize,
                    false,
                    false,
                    transaction);

                // Create the clustered index
                sql = GetCreateTemporaryTableClusteredIndexSqlText(tempTableName,
                    qualifiers,
                    dbSetting);
                connection.ExecuteNonQuery(sql, transaction: transaction);

                // Update the actual update
                sql = GetBulkUpdateSqlText(tableName,
                    tempTableName,
                    fields,
                    qualifiers,
                    primaryDbField?.AsField(),
                    identityDbField?.AsField(),
                    hints,
                    dbSetting);
                result = connection.ExecuteNonQuery(sql, commandTimeout: bulkCopyTimeout, transaction: transaction);

                // Drop the table after used
                sql = GetDropTemporaryTableSqlText(tempTableName, dbSetting);
                connection.ExecuteNonQuery(sql, transaction: transaction);

                // Commit the transaction
                if (hasTransaction == false)
                {
                    transaction?.Commit();
                }
            }
            catch
            {
                // Rollback the transaction
                if (hasTransaction == false)
                {
                    transaction?.Rollback();
                }

                // Throw
                throw;
            }
            finally
            {
                // Dispose the transaction
                if (hasTransaction == false)
                {
                    transaction?.Dispose();
                }
            }

            // Result
            return result;
        }

        #endregion

        #region BulkUpdateAsyncInternalBase

        /// <summary>
        /// 
        /// </summary>
        /// <typeparam name="TSqlBulkCopy"></typeparam>
        /// <typeparam name="TSqlBulkCopyOptions"></typeparam>
        /// <typeparam name="TSqlBulkCopyColumnMappingCollection"></typeparam>
        /// <typeparam name="TSqlBulkCopyColumnMapping"></typeparam>
        /// <typeparam name="TSqlTransaction"></typeparam>
        /// <param name="connection"></param>
        /// <param name="tableName"></param>
        /// <param name="reader"></param>
        /// <param name="qualifiers"></param>
        /// <param name="mappings"></param>
        /// <param name="options"></param>
        /// <param name="hints"></param>
        /// <param name="bulkCopyTimeout"></param>
        /// <param name="batchSize"></param>
        /// <param name="usePhysicalPseudoTempTable"></param>
        /// <param name="transaction"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        internal static async Task<int> BulkUpdateAsyncInternalBase<TSqlBulkCopy, TSqlBulkCopyOptions, TSqlBulkCopyColumnMappingCollection,
            TSqlBulkCopyColumnMapping, TSqlTransaction>(DbConnection connection,
            string tableName,
            DbDataReader reader,
            IEnumerable<Field> qualifiers = null,
            IEnumerable<BulkInsertMapItem> mappings = null,
            TSqlBulkCopyOptions options = default,
            string hints = null,
            int? bulkCopyTimeout = null,
            int? batchSize = null,
            bool? usePhysicalPseudoTempTable = null,
            TSqlTransaction transaction = null,
            CancellationToken cancellationToken = default)
            where TSqlBulkCopy : class, IDisposable
            where TSqlBulkCopyOptions : Enum
            where TSqlBulkCopyColumnMappingCollection : class
            where TSqlBulkCopyColumnMapping : class
            where TSqlTransaction : DbTransaction
        {
            // Validate
            if (!reader.HasRows)
            {
                return default;
            }

            // Variables
            var dbSetting = connection.GetDbSetting();
            var hasTransaction = (transaction != null);
            var result = default(int);

            // Check the transaction
            if (transaction == null)
            {
                // Add the transaction if not present
                transaction = (TSqlTransaction)(await connection.EnsureOpenAsync(cancellationToken)).BeginTransaction();
            }
            else
            {
                // Validate the objects
                SqlConnectionExtension.ValidateTransactionConnectionObject(connection, transaction);
            }

            // Before Execution Time
            var beforeExecutionTime = DateTime.UtcNow;

            // Must be fixed name so the RepoDb.Core caches will not be bloated
            var tempTableName = string.Concat("_RepoDb_BulkUpdate_", GetTableName(tableName, dbSetting));

            // Add a # prefix if not physical
            if (usePhysicalPseudoTempTable != true)
            {
                tempTableName = string.Concat("#", tempTableName);
            }

            try
            {
                // Get the DB Fields
                var dbFields = await DbFieldCache.GetAsync(connection, tableName, transaction, true, cancellationToken);

                // Variables needed
                var readerFields = Enumerable.Range(0, reader.FieldCount)
                    .Select((index) => reader.GetName(index));
                var fields = dbFields?.Select(dbField => dbField.AsField());
                var primaryDbField = dbFields?.FirstOrDefault(dbField => dbField.IsPrimary);
                var identityDbField = dbFields?.FirstOrDefault(dbField => dbField.IsIdentity);
                var primaryOrIdentityDbField = (primaryDbField ?? identityDbField);

                // Validate the primary keys
                if (qualifiers?.Any() != true)
                {
                    if (primaryOrIdentityDbField == null)
                    {
                        throw new MissingPrimaryKeyException($"No primary key or identity key found for table '{tableName}'.");
                    }
                    else
                    {
                        qualifiers = new[] { primaryOrIdentityDbField.AsField() };
                    }
                }

                // Filter the fields (based on the data reader)
                if (readerFields?.Any() == true)
                {
                    fields = fields
                        .Where(e =>
                            readerFields.Any(fieldName => string.Equals(fieldName, e.Name, StringComparison.OrdinalIgnoreCase)) == true);
                }

                // Filter the fields (based on the mappings)
                if (mappings?.Any() == true)
                {
                    fields = fields
                        .Where(e =>
                            mappings.Any(m => string.Equals(m.SourceColumn, e.Name, StringComparison.OrdinalIgnoreCase)) == true);
                }

                // Throw an error if there are no fields
                if (fields?.Any() != true)
                {
                    throw new MissingFieldException("There are no field(s) found for this operation.");
                }

                // Create a temporary table
                var sql = GetCreateTemporaryTableSqlText(tableName,
                    tempTableName,
                    fields,
                    dbSetting);
                await connection.ExecuteNonQueryAsync(sql, transaction: transaction, cancellationToken: cancellationToken);

                // Set the options to KeepIdentity if needed
                if (object.Equals(options, default(TSqlBulkCopyOptions)) &&
                    identityDbField?.IsIdentity == true &&
                    fields?.FirstOrDefault(
                        field => string.Equals(field.Name, identityDbField.Name, StringComparison.OrdinalIgnoreCase)) != null)
                {
                    options = Compiler.GetEnumFunc<TSqlBulkCopyOptions>("KeepIdentity")();
                }

                // Filter the DB Fields
                var filteredDbFields = dbFields?
                    .Where(dbField =>
                        fields?.Any(field => string.Equals(field.Name, dbField.Name, StringComparison.OrdinalIgnoreCase)) == true);

                // Do the bulk insertion first
                await BulkInsertAsyncInternalBase<TSqlBulkCopy, TSqlBulkCopyOptions, TSqlBulkCopyColumnMappingCollection,
                    TSqlBulkCopyColumnMapping, TSqlTransaction>(connection,
                    tempTableName,
                    reader,
                    filteredDbFields,
                    mappings,
                    options,
                    hints,
                    bulkCopyTimeout,
                    batchSize,
                    false,
                    false,
                    transaction,
                    cancellationToken);

                // Create the clustered index
                sql = GetCreateTemporaryTableClusteredIndexSqlText(tempTableName,
                    qualifiers,
                    dbSetting);
                await connection.ExecuteNonQueryAsync(sql, transaction: transaction, cancellationToken: cancellationToken);

                // Update the actual update
                sql = GetBulkUpdateSqlText(tableName,
                    tempTableName,
                    fields,
                    qualifiers,
                    primaryDbField?.AsField(),
                    identityDbField?.AsField(),
                    hints,
                    dbSetting);
                result = await connection.ExecuteNonQueryAsync(sql, commandTimeout: bulkCopyTimeout, transaction: transaction, cancellationToken: cancellationToken);

                // Drop the table after used
                sql = GetDropTemporaryTableSqlText(tempTableName, dbSetting);
                await connection.ExecuteNonQueryAsync(sql, transaction: transaction, cancellationToken: cancellationToken);

                // Commit the transaction
                if (hasTransaction == false)
                {
                    transaction?.Commit();
                }
            }
            catch
            {
                // Rollback the transaction
                if (hasTransaction == false)
                {
                    transaction?.Rollback();
                }

                // Throw
                throw;
            }
            finally
            {
                // Dispose the transaction
                if (hasTransaction == false)
                {
                    transaction?.Dispose();
                }
            }

            // Result
            return result;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <typeparam name="TSqlBulkCopy"></typeparam>
        /// <typeparam name="TSqlBulkCopyOptions"></typeparam>
        /// <typeparam name="TSqlBulkCopyColumnMappingCollection"></typeparam>
        /// <typeparam name="TSqlBulkCopyColumnMapping"></typeparam>
        /// <typeparam name="TSqlTransaction"></typeparam>
        /// <param name="connection"></param>
        /// <param name="tableName"></param>
        /// <param name="dataTable"></param>
        /// <param name="qualifiers"></param>
        /// <param name="rowState"></param>
        /// <param name="mappings"></param>
        /// <param name="options"></param>
        /// <param name="hints"></param>
        /// <param name="bulkCopyTimeout"></param>
        /// <param name="batchSize"></param>
        /// <param name="usePhysicalPseudoTempTable"></param>
        /// <param name="transaction"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        internal static async Task<int> BulkUpdateAsyncInternalBase<TSqlBulkCopy, TSqlBulkCopyOptions, TSqlBulkCopyColumnMappingCollection,
            TSqlBulkCopyColumnMapping, TSqlTransaction>(DbConnection connection,
            string tableName,
            DataTable dataTable,
            IEnumerable<Field> qualifiers = null,
            DataRowState? rowState = null,
            IEnumerable<BulkInsertMapItem> mappings = null,
            TSqlBulkCopyOptions options = default,
            string hints = null,
            int? bulkCopyTimeout = null,
            int? batchSize = null,
            bool? usePhysicalPseudoTempTable = null,
            TSqlTransaction transaction = null,
            CancellationToken cancellationToken = default)
            where TSqlBulkCopy : class, IDisposable
            where TSqlBulkCopyOptions : Enum
            where TSqlBulkCopyColumnMappingCollection : class
            where TSqlBulkCopyColumnMapping : class
            where TSqlTransaction : DbTransaction
        {
            // Validate
            if (dataTable?.Rows?.Count <= 0)
            {
                return default;
            }

            // Variables
            var dbSetting = connection.GetDbSetting();
            var hasTransaction = (transaction != null);
            var result = default(int);

            // Check the transaction
            if (transaction == null)
            {
                // Add the transaction if not present
                transaction = (TSqlTransaction)(await connection.EnsureOpenAsync(cancellationToken)).BeginTransaction();
            }
            else
            {
                // Validate the objects
                SqlConnectionExtension.ValidateTransactionConnectionObject(connection, transaction);
            }

            // Before Execution Time
            var beforeExecutionTime = DateTime.UtcNow;

            // Must be fixed name so the RepoDb.Core caches will not be bloated
            var tempTableName = string.Concat("_RepoDb_BulkUpdate_", GetTableName(tableName, dbSetting));

            // Add a # prefix if not physical
            if (usePhysicalPseudoTempTable != true)
            {
                tempTableName = string.Concat("#", tempTableName);
            }

            try
            {
                // Get the DB Fields
                var dbFields = await DbFieldCache.GetAsync(connection, tableName, transaction, true, cancellationToken);

                // Variables needed
                var tableFields = Enumerable.Range(0, dataTable.Columns.Count)
                    .Select((index) => dataTable.Columns[index].ColumnName);
                var fields = dbFields?.Select(dbField => dbField.AsField());
                var primaryDbField = dbFields?.FirstOrDefault(dbField => dbField.IsPrimary);
                var identityDbField = dbFields?.FirstOrDefault(dbField => dbField.IsIdentity);
                var primaryOrIdentityDbField = (primaryDbField ?? identityDbField);

                // Validate the primary keys
                if (qualifiers?.Any() != true)
                {
                    if (primaryOrIdentityDbField == null)
                    {
                        throw new MissingPrimaryKeyException($"No primary key or identity key found for table '{tableName}'.");
                    }
                    else
                    {
                        qualifiers = new[] { primaryOrIdentityDbField.AsField() };
                    }
                }

                // Filter the fields (based on the data table)
                if (tableFields?.Any() == true)
                {
                    fields = fields
                        .Where(e =>
                            tableFields.Any(fieldName => string.Equals(fieldName, e.Name, StringComparison.OrdinalIgnoreCase)) == true);
                }

                // Filter the fields (based on the mappings)
                if (mappings?.Any() == true)
                {
                    fields = fields
                        .Where(e =>
                            mappings.Any(m => string.Equals(m.SourceColumn, e.Name, StringComparison.OrdinalIgnoreCase)) == true);
                }

                // Throw an error if there are no fields
                if (fields?.Any() != true)
                {
                    throw new MissingFieldException("There are no field(s) found for this operation.");
                }

                // Create a temporary table
                var sql = GetCreateTemporaryTableSqlText(tableName,
                    tempTableName,
                    fields,
                    dbSetting);
                await connection.ExecuteNonQueryAsync(sql, transaction: transaction, cancellationToken: cancellationToken);

                // Set the options to KeepIdentity if needed
                if (object.Equals(options, default(TSqlBulkCopyOptions)) &&
                    identityDbField?.IsIdentity == true &&
                    fields?.FirstOrDefault(
                        field => string.Equals(field.Name, identityDbField.Name, StringComparison.OrdinalIgnoreCase)) != null)
                {
                    options = Compiler.GetEnumFunc<TSqlBulkCopyOptions>("KeepIdentity")();
                }

                // Filter the DB Fields
                var filteredDbFields = dbFields?
                    .Where(dbField =>
                        fields?.Any(field => string.Equals(field.Name, dbField.Name, StringComparison.OrdinalIgnoreCase)) == true);

                // Do the bulk insertion first
                await BulkInsertAsyncInternalBase<TSqlBulkCopy, TSqlBulkCopyOptions, TSqlBulkCopyColumnMappingCollection,
                    TSqlBulkCopyColumnMapping, TSqlTransaction>(connection,
                    tempTableName,
                    dataTable,
                    rowState,
                    filteredDbFields,
                    mappings,
                    options,
                    hints,
                    bulkCopyTimeout,
                    batchSize,
                    false,
                    false,
                    transaction,
                    cancellationToken);

                // Create the clustered index
                sql = GetCreateTemporaryTableClusteredIndexSqlText(tempTableName,
                    qualifiers,
                    dbSetting);
                await connection.ExecuteNonQueryAsync(sql, transaction: transaction, cancellationToken: cancellationToken);

                // Update the actual update
                sql = GetBulkUpdateSqlText(tableName,
                    tempTableName,
                    fields,
                    qualifiers,
                    primaryDbField?.AsField(),
                    identityDbField?.AsField(),
                    hints,
                    dbSetting);
                result = await connection.ExecuteNonQueryAsync(sql, commandTimeout: bulkCopyTimeout, transaction: transaction, cancellationToken: cancellationToken);

                // Drop the table after used
                sql = GetDropTemporaryTableSqlText(tempTableName, dbSetting);
                await connection.ExecuteNonQueryAsync(sql, transaction: transaction, cancellationToken: cancellationToken);

                // Commit the transaction
                if (hasTransaction == false)
                {
                    transaction?.Commit();
                }
            }
            catch
            {
                // Rollback the transaction
                if (hasTransaction == false)
                {
                    transaction?.Rollback();
                }

                // Throw
                throw;
            }
            finally
            {
                // Dispose the transaction
                if (hasTransaction == false)
                {
                    transaction?.Dispose();
                }
            }

            // Result
            return result;
        }

        #endregion
    }
}
