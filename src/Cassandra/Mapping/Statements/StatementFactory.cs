﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Cassandra.Mapping.Statements
{
    /// <summary>
    /// Creates statements from CQL that can be executed with the C* driver.
    /// </summary>
    internal class StatementFactory
    {
        private readonly ConcurrentDictionary<string, Task<PreparedStatement>> _statementCache;
        private static readonly Logger Logger = new Logger(typeof(StatementFactory));

        public int MaxPreparedStatementsThreshold { get; set; }

        public StatementFactory()
        {
            MaxPreparedStatementsThreshold = 500;
            _statementCache = new ConcurrentDictionary<string, Task<PreparedStatement>>();
        }

        public Task<Statement> GetStatementAsync(ISession session, Cql cql)
        {
            // Use a SimpleStatement if we're not supposed to prepare
            if (cql.QueryOptions.NoPrepare)
            {
                Statement statement = new SimpleStatement(cql.Statement, cql.Arguments);
                cql.QueryOptions.CopyOptionsToStatement(statement);
                return TaskHelper.ToTask(statement);
            }
            return _statementCache
                .GetOrAdd(cql.Statement, session.PrepareAsync)
                .Continue(t =>
                {
                    if (_statementCache.Count > MaxPreparedStatementsThreshold)
                    {
                        Logger.Warning(String.Format("The prepared statement cache contains {0} queries. Use parameter markers for queries. You can configure this warning threshold using MappingConfiguration.SetMaxStatementPreparedThreshold() method.", _statementCache.Count));
                    }
                    var boundStatement = t.Result.Bind(cql.Arguments);
                    cql.QueryOptions.CopyOptionsToStatement(boundStatement);
                    return (Statement)boundStatement;
                });
        }

        public Statement GetStatement(ISession session, Cql cql)
        {
            // Just use async version's result
            return GetStatementAsync(session, cql).Result;
        }

        public Task<BatchStatement> GetBatchStatementAsync(ISession session, IEnumerable<Cql> cqlToBatch)
        {
            // Get all the statements async in parallel, then add to batch
            return Task.Factory.ContinueWhenAll(cqlToBatch.Select(cql => GetStatementAsync(session, cql)).ToArray(), (tasks) =>
            {
                var batch = new BatchStatement();
                foreach (var t in tasks)
                {
                    if (t.Exception != null)
                    {
                        throw t.Exception;
                    }
                    batch.Add(t.Result);
                }
                return batch;
            }, TaskContinuationOptions.ExecuteSynchronously);
        }

        public BatchStatement GetBatchStatement(ISession session, IEnumerable<Cql> cqlToBatch)
        {
            var batch = new BatchStatement();
            foreach (var cql in cqlToBatch)
            {
                batch.Add(GetStatement(session, cql));
            }
            return batch;
        }
    }
}
