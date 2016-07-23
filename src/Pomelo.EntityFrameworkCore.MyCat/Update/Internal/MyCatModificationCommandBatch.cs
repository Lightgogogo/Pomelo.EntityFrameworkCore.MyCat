﻿// Copyright (c) Pomelo Foundation. All rights reserved.
// Licensed under the MIT. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Reflection;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Utilities;
using Pomelo.Data.MySql;

namespace Microsoft.EntityFrameworkCore.Update.Internal
{
    using RelationalStrings = Microsoft.EntityFrameworkCore.Internal.RelationalStrings;
    public class MyCatModificationCommandBatch : AffectedCountModificationCommandBatch
    {

        private const int DefaultNetworkPacketSizeBytes = 4096;
        private const int MaxScriptLength = 65536 * DefaultNetworkPacketSizeBytes / 2;
        private const int MaxParameterCount = 2100;
        private const int MaxRowCount = 1000;
        private int _parameterCount = 1; // Implicit parameter for the command text
        private readonly int _maxBatchSize;
        private readonly List<ModificationCommand> _bulkInsertCommands = new List<ModificationCommand>();
        private int _commandsLeftToLengthCheck = 50;

        public MyCatModificationCommandBatch(
            [NotNull] IRelationalCommandBuilderFactory commandBuilderFactory,
            [NotNull] ISqlGenerationHelper SqlGenerationHelper,
            [NotNull] IMyCatUpdateSqlGenerator updateSqlGenerator,
            [NotNull] IRelationalValueBufferFactoryFactory valueBufferFactoryFactory,
            [CanBeNull] int? maxBatchSize)
            : base(commandBuilderFactory, SqlGenerationHelper, updateSqlGenerator, valueBufferFactoryFactory)
        {
            if (maxBatchSize.HasValue
                && (maxBatchSize.Value <= 0))
            {
                throw new ArgumentOutOfRangeException(nameof(maxBatchSize), RelationalStrings.InvalidMaxBatchSize);
            }

            _maxBatchSize = Math.Min(maxBatchSize ?? int.MaxValue, MaxRowCount);
        }

        protected new virtual IMyCatUpdateSqlGenerator UpdateSqlGenerator => (IMyCatUpdateSqlGenerator)base.UpdateSqlGenerator;


        protected override bool CanAddCommand(ModificationCommand modificationCommand)
        {
            if (_maxBatchSize <= ModificationCommands.Count)
            {
                return false;
            }

            var additionalParameterCount = CountParameters(modificationCommand);

            if (_parameterCount + additionalParameterCount >= MaxParameterCount)
            {
                return false;
            }

            _parameterCount += additionalParameterCount;
            return true;
        }

        private static int CountParameters(ModificationCommand modificationCommand)
        {
            var parameterCount = 0;
            foreach (var columnModification in modificationCommand.ColumnModifications)
            {
                if (columnModification.ParameterName != null)
                {
                    parameterCount++;
                }

                if (columnModification.OriginalParameterName != null)
                {
                    parameterCount++;
                }
            }

            return parameterCount;
        }

        protected override void ResetCommandText()
        {
            base.ResetCommandText();
            _bulkInsertCommands.Clear();
        }

        protected override bool IsCommandTextValid()
        {
            if (--_commandsLeftToLengthCheck < 0)
            {
                var commandTextLength = GetCommandText().Length;
                if (commandTextLength >= MaxScriptLength)
                {
                    return false;
                }

                var avarageCommandLength = commandTextLength / ModificationCommands.Count;
                var expectedAdditionalCommandCapacity = (MaxScriptLength - commandTextLength) / avarageCommandLength;
                _commandsLeftToLengthCheck = Math.Max(1, expectedAdditionalCommandCapacity / 4);
            }

            return true;
        }
        protected override string GetCommandText()
            => base.GetCommandText() + GetBulkInsertCommandText(ModificationCommands.Count);

        private string GetBulkInsertCommandText(int lastIndex)
        {
            if (_bulkInsertCommands.Count == 0)
            {
                return string.Empty;
            }

            var stringBuilder = new StringBuilder();
            var grouping = UpdateSqlGenerator.AppendBulkInsertOperation(stringBuilder, _bulkInsertCommands, lastIndex);
            for (var i = lastIndex - _bulkInsertCommands.Count; i < lastIndex; i++)
            {
                CommandResultSet[i] = grouping;
            }

            if (grouping != ResultSetMapping.NoResultSet)
            {
                CommandResultSet[lastIndex - 1] = ResultSetMapping.LastInResultSet;
            }

            return stringBuilder.ToString();
        }

        protected override void UpdateCachedCommandText(int commandPosition)
        {
            var newModificationCommand = ModificationCommands[commandPosition];

            if (newModificationCommand.EntityState == EntityState.Added)
            {
                if ((_bulkInsertCommands.Count > 0)
                    && !CanBeInsertedInSameStatement(_bulkInsertCommands[0], newModificationCommand))
                {
                    CachedCommandText.Append(GetBulkInsertCommandText(commandPosition));
                    _bulkInsertCommands.Clear();
                }
                _bulkInsertCommands.Add(newModificationCommand);

                LastCachedCommandIndex = commandPosition;
            }
            else
            {
                CachedCommandText.Append(GetBulkInsertCommandText(commandPosition));
                _bulkInsertCommands.Clear();

                base.UpdateCachedCommandText(commandPosition);
            }
        }

        private static bool CanBeInsertedInSameStatement(ModificationCommand firstCommand, ModificationCommand secondCommand)
            => string.Equals(firstCommand.TableName, secondCommand.TableName, StringComparison.Ordinal)
               && string.Equals(firstCommand.Schema, secondCommand.Schema, StringComparison.Ordinal)
               && firstCommand.ColumnModifications.Where(o => o.IsWrite).Select(o => o.ColumnName).SequenceEqual(
                   secondCommand.ColumnModifications.Where(o => o.IsWrite).Select(o => o.ColumnName))
               && firstCommand.ColumnModifications.Where(o => o.IsRead).Select(o => o.ColumnName).SequenceEqual(
                   secondCommand.ColumnModifications.Where(o => o.IsRead).Select(o => o.ColumnName));

        protected override void Consume(DbDataReader reader)
        {
            Debug.Assert(CommandResultSet.Count == ModificationCommands.Count);
            var commandIndex = 0;

            try
            {
                var actualResultSetCount = 0;
                do
                {
                    while (commandIndex < CommandResultSet.Count
                           && CommandResultSet[commandIndex] == ResultSetMapping.NoResultSet)
                    {
                        commandIndex++;
                    }

                    if (commandIndex < CommandResultSet.Count)
                    {
                        commandIndex = ModificationCommands[commandIndex].RequiresResultPropagation
                            ? ConsumeResultSetWithPropagation(commandIndex, reader)
                            : ConsumeResultSetWithoutPropagation(commandIndex, reader);
                        actualResultSetCount++;
                    }
                }
                while (commandIndex < CommandResultSet.Count
                       && reader.NextResult());

            }
            catch (DbUpdateException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new DbUpdateException(
                    RelationalStrings.UpdateStoreException,
                    ex,
                    ModificationCommands[commandIndex].Entries);
            }
        }

        protected override async Task ConsumeAsync(
            DbDataReader reader,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            Debug.Assert(CommandResultSet.Count == ModificationCommands.Count);
            var commandIndex = 0;

            try
            {
                var actualResultSetCount = 0;
                do
                {
                    while (commandIndex < CommandResultSet.Count
                           && CommandResultSet[commandIndex] == ResultSetMapping.NoResultSet)
                    {
                        commandIndex++;
                    }

                    if (commandIndex < CommandResultSet.Count)
                    {
                        commandIndex = ModificationCommands[commandIndex].RequiresResultPropagation
                            ? await ConsumeResultSetWithPropagationAsync(commandIndex, reader, cancellationToken)
                            : await ConsumeResultSetWithoutPropagationAsync(commandIndex, reader, cancellationToken);
                        actualResultSetCount++;
                    }
                }
                while (commandIndex < CommandResultSet.Count
                       && await reader.NextResultAsync(cancellationToken));
            }
            catch (DbUpdateException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new DbUpdateException(
                    RelationalStrings.UpdateStoreException,
                    ex,
                    ModificationCommands[commandIndex].Entries);
            }
        }

        protected override int ConsumeResultSetWithPropagation(int commandIndex, [NotNull] DbDataReader reader)
        {
            var rowsAffected = ((MySqlDataReader)reader).RecordsAffected;
            do
            {
                var tableModification = ModificationCommands[commandIndex];
                Debug.Assert(tableModification.RequiresResultPropagation);

                var valueBufferFactory = CreateValueBufferFactory(tableModification.ColumnModifications);

                if (commandIndex < ModificationCommands.Count)
                {
                    if (ModificationCommands[commandIndex].EntityState != EntityState.Added)
                        tableModification.PropagateResults(valueBufferFactory.Create(reader));
                    else
                    {
                        try
                        {
                            var prop = tableModification.GetType().GetTypeInfo().DeclaredFields.Single(x => x.Name == "_columnModifications");
                            var cm = (IReadOnlyList<ColumnModification>)prop.GetValue(tableModification);
                            var entries = cm.Where(x => x.IsKey);
                            foreach (var x in entries)
                            {
                                if (x.Value.GetType() == typeof(int))
                                    x.Value = Convert.ToInt32(((MySqlDataReader)reader).command.LastInsertedId);
                                else
                                    x.Value = ((MySqlDataReader)reader).command.LastInsertedId;
                            }
                        }
                        catch { }
                    }
                }
            }
            while ((++commandIndex < CommandResultSet.Count)
                   && CommandResultSet[commandIndex - 1] == ResultSetMapping.NotLastInResultSet);

            return commandIndex;
        }

        protected override async Task<int> ConsumeResultSetWithPropagationAsync(
            int commandIndex, [NotNull] DbDataReader reader, CancellationToken cancellationToken)
        {
            var rowsAffected = ((MySqlDataReader)reader).RecordsAffected;
            do
            {
                var tableModification = ModificationCommands[commandIndex];
                Debug.Assert(tableModification.RequiresResultPropagation);

                var valueBufferFactory = CreateValueBufferFactory(tableModification.ColumnModifications);

                if (commandIndex < ModificationCommands.Count)
                {
                    if (ModificationCommands[commandIndex].EntityState != EntityState.Added)
                        tableModification.PropagateResults(valueBufferFactory.Create(reader));
                    else
                    {
                        try
                        {
                            var prop = tableModification.GetType().GetTypeInfo().GetField("_columnModifications");
                            var cm = (IReadOnlyList<ColumnModification>)prop.GetValue(tableModification);
                            var entries = cm.Where(x => x.IsKey);
                            foreach (var x in entries)
                                x.Value = ((MySqlDataReader)reader).command.LastInsertedId;
                        }
                        catch { }
                    }
                }
            }
            while ((++commandIndex < CommandResultSet.Count)
                   && CommandResultSet[commandIndex - 1] == ResultSetMapping.NotLastInResultSet);

            return commandIndex;
        }

        protected override int ConsumeResultSetWithoutPropagation(int commandIndex, [NotNull] DbDataReader reader)
        {
            var expectedRowsAffected = 1;
            while ((++commandIndex < CommandResultSet.Count)
                   && CommandResultSet[commandIndex - 1] == ResultSetMapping.NotLastInResultSet)
            {
                Debug.Assert(!ModificationCommands[commandIndex].RequiresResultPropagation);

                expectedRowsAffected++;
            }
            
            if (reader.RecordsAffected != expectedRowsAffected)
            {
                ThrowAggregateUpdateConcurrencyException(commandIndex, expectedRowsAffected, reader.RecordsAffected);
            }

            return commandIndex;
        }

        protected override async Task<int> ConsumeResultSetWithoutPropagationAsync(
            int commandIndex, [NotNull] DbDataReader reader, CancellationToken cancellationToken)
        {
            var expectedRowsAffected = 1;
            while ((++commandIndex < CommandResultSet.Count)
                   && CommandResultSet[commandIndex - 1] == ResultSetMapping.NotLastInResultSet)
            {
                Debug.Assert(!ModificationCommands[commandIndex].RequiresResultPropagation);

                expectedRowsAffected++;
            }

            if (reader.RecordsAffected != expectedRowsAffected)
            {
                ThrowAggregateUpdateConcurrencyException(commandIndex, expectedRowsAffected, reader.RecordsAffected);
            }

            return commandIndex;
        }

        /*protected override void Consume(DbDataReader reader)
        {
            var MyCatReader = (MyCatDataReader)reader;
            Debug.Assert(MyCatReader.Statements.Count == ModificationCommands.Count, $"Reader has {MyCatReader.Statements.Count} statements, expected {ModificationCommands.Count}");
            var commandIndex = 0;

            try
            {
                while (true)
                {
                    // Find the next propagating command, if any
                    int nextPropagating;
                    for (nextPropagating = commandIndex;
                        nextPropagating < ModificationCommands.Count &&
                        !ModificationCommands[nextPropagating].RequiresResultPropagation;
                        nextPropagating++) ;

                    // Go over all non-propagating commands before the next propagating one,
                    // make sure they executed
                    for (; commandIndex < nextPropagating; commandIndex++)
                    {
                        if (MyCatReader.Statements[commandIndex].Rows == 0)
                        {
                            throw new DbUpdateConcurrencyException(
                                RelationalStrings.UpdateConcurrencyException(1, 0),
                                ModificationCommands[commandIndex].Entries
                            );
                        }
                    }

                    if (nextPropagating == ModificationCommands.Count)
                    {
                        Debug.Assert(!reader.NextResult(), "Expected less resultsets");
                        break;
                    }

                    // Propagate to results from the reader to the ModificationCommand

                    var modificationCommand = ModificationCommands[commandIndex++];

                    if (!reader.Read())
                    {
                        throw new DbUpdateConcurrencyException(
                            RelationalStrings.UpdateConcurrencyException(1, 0),
                            modificationCommand.Entries);
                    }

                    var valueBufferFactory = CreateValueBufferFactory(modificationCommand.ColumnModifications);
                    modificationCommand.PropagateResults(valueBufferFactory.Create(reader));

                    reader.NextResult();
                }
            }
            catch (DbUpdateException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new DbUpdateException(
                    RelationalStrings.UpdateStoreException,
                    ex,
                    ModificationCommands[commandIndex].Entries);
            }
        }*/

        /*protected override async Task ConsumeAsync(
            DbDataReader reader,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var MyCatReader = (MyCatDataReader)reader;
            Debug.Assert(MyCatReader.Statements.Count == ModificationCommands.Count, $"Reader has {MyCatReader.Statements.Count} statements, expected {ModificationCommands.Count}");
            var commandIndex = 0;

            try
            {
                while (true)
                {
                    // Find the next propagating command, if any
                    int nextPropagating;
                    for (nextPropagating = commandIndex;
                        nextPropagating < ModificationCommands.Count &&
                        !ModificationCommands[nextPropagating].RequiresResultPropagation;
                        nextPropagating++)
                        ;

                    // Go over all non-propagating commands before the next propagating one,
                    // make sure they executed
                    for (; commandIndex < nextPropagating; commandIndex++)
                    {
                        if (MyCatReader.Statements[commandIndex].Rows == 0)
                        {
                            throw new DbUpdateConcurrencyException(
                                RelationalStrings.UpdateConcurrencyException(1, 0),
                                ModificationCommands[commandIndex].Entries
                            );
                        }
                    }

                    if (nextPropagating == ModificationCommands.Count)
                    {
                        Debug.Assert(!(await reader.NextResultAsync(cancellationToken)), "Expected less resultsets");
                        break;
                    }

                    // Extract result from the command and propagate it

                    var modificationCommand = ModificationCommands[commandIndex++];

                    if (!(await reader.ReadAsync(cancellationToken)))
                    {
                        throw new DbUpdateConcurrencyException(
                            RelationalStrings.UpdateConcurrencyException(1, 0),
                            modificationCommand.Entries
                        );
                    }

                    var valueBufferFactory = CreateValueBufferFactory(modificationCommand.ColumnModifications);
                    modificationCommand.PropagateResults(valueBufferFactory.Create(reader));

                    await reader.NextResultAsync(cancellationToken);
                }
            }
            catch (DbUpdateException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new DbUpdateException(
                    RelationalStrings.UpdateStoreException,
                    ex,
                    ModificationCommands[commandIndex].Entries);
            }
        }*/
    }
}
