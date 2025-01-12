﻿using Microsoft.AnalysisServices.AdomdClient;
using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.Data.SqlTypes;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Tom = Microsoft.AnalysisServices.Tabular;

namespace Dax.Metadata.Extractor
{
    public class TomExtractor
    {
        protected Dax.Metadata.Model DaxModel { get; private set; }
        protected Tom.Model tomModel;
        private TomExtractor(Tom.Model model, string extractorApp = null, string extractorVersion = null)
        {
            tomModel = model;

            var extractorInfo = Util.GetExtractorInfo(this);
            DaxModel = new Dax.Metadata.Model(extractorInfo.Name, extractorInfo.Version, extractorApp, extractorVersion);

            if (tomModel != null) {
                PopulateModel();
            }
        }
        
        private void PopulateModel()
        {
            foreach (Tom.Table table in tomModel.Tables) {
                AddTable(table);
            }
           
            foreach (Tom.SingleColumnRelationship relationship in tomModel.Relationships) {
                AddRelationship(relationship);
            }
            foreach (Tom.ModelRole role in tomModel.Roles) {
                AddRole(role);
            }

            // Specific model properties
            DaxModel.DefaultMode = (Partition.PartitionMode)tomModel.DefaultMode;
            DaxModel.Culture = tomModel.Culture;

            // Compatibility Level and Mode
            DaxModel.CompatibilityLevel = tomModel.Database.CompatibilityLevel;
            DaxModel.CompatibilityMode = tomModel.Database.CompatibilityMode.ToString();

            // Database version and last update and process date and time
            DaxModel.LastProcessed = tomModel.Database.LastProcessed;
            DaxModel.LastUpdate = tomModel.Database.LastUpdate;
            DaxModel.Version = tomModel.Database.Version;

            // Update ExtractionDate
            DaxModel.ExtractionDate = DateTime.UtcNow;
        }

        private void AddRole( Tom.ModelRole role )
        {
            Dax.Metadata.Role daxRole = new(DaxModel)
            {
                RoleName = new DaxName(role.Name)
            };
            foreach (Tom.TablePermission tablePermission in role.TablePermissions ) {
                Dax.Metadata.Table table = DaxModel.Tables.SingleOrDefault(t => t.TableName.Name == tablePermission.Table.Name);
                Dax.Metadata.TablePermission daxTablePermission = new(daxRole)
                {
                    Table = table,
                    FilterExpression = DaxExpression.GetExpression(tablePermission.FilterExpression)
                };

                daxRole.TablePermissions.Add(daxTablePermission);
            }

            DaxModel.Roles.Add(daxRole);
        }

        private void AddRelationship(Tom.SingleColumnRelationship relationship)
        {
            Dax.Metadata.Table fromTable = DaxModel.Tables.SingleOrDefault(t => t.TableName.Name == relationship.FromTable.Name);
            Dax.Metadata.Column fromColumn = fromTable.Columns.SingleOrDefault(t => t.ColumnName.Name == relationship.FromColumn.Name);
            Dax.Metadata.Table toTable = DaxModel.Tables.SingleOrDefault(t => t.TableName.Name == relationship.ToTable.Name);
            Dax.Metadata.Column toColumn = toTable.Columns.SingleOrDefault(t => t.ColumnName.Name == relationship.ToColumn.Name);
            Dax.Metadata.Relationship daxRelationship = new(fromColumn, toColumn )
            {
                FromCardinalityType = relationship.FromCardinality.ToString(),
                ToCardinalityType = relationship.ToCardinality.ToString(),
                RelyOnReferentialIntegrity = relationship.RelyOnReferentialIntegrity,
                JoinOnDateBehavior = relationship.JoinOnDateBehavior.ToString(),
                CrossFilteringBehavior = relationship.CrossFilteringBehavior.ToString(),
                Type = relationship.Type.ToString(),
                IsActive = relationship.IsActive,
                Name = relationship.Name,
                SecurityFilteringBehavior = relationship.SecurityFilteringBehavior.ToString()
            };
            DaxModel.Relationships.Add(daxRelationship);
        }

        private void AddTable(Tom.Table table)
        {
            Tom.PartitionCollection partitions = table.Partitions;
            Tom.PartitionSourceType tableType = (partitions?.Count > 0) ? (partitions[0].SourceType) : Tom.PartitionSourceType.None;
            bool isCalculatedTable = (tableType == Tom.PartitionSourceType.Calculated);
            bool isCalculationGroup = (tableType == Tom.PartitionSourceType.CalculationGroup);
            Tom.CalculatedPartitionSource partitionSource = (isCalculatedTable) ? partitions[0].Source as Tom.CalculatedPartitionSource : null;

            Dax.Metadata.Table daxTable = new(DaxModel)
            {
                TableName = new Dax.Metadata.DaxName(table.Name),
                IsHidden = table.IsHidden,
                IsPrivate = table.IsPrivate,
                IsLocalDateTable = (table.Annotations.FirstOrDefault(a => a.Name == "__PBI_LocalDateTable" && a.Value == "true") != null),
                IsTemplateDateTable = (table.Annotations.FirstOrDefault(a => a.Name == "__PBI_TemplateDateTable" && a.Value == "true") != null),
                TableExpression = Dax.Metadata.DaxExpression.GetExpression(isCalculatedTable ? partitionSource.Expression : null),
                TableType = isCalculatedTable ? Table.TableSourceType.CalculatedTable.ToString() :
                       (isCalculationGroup ? Table.TableSourceType.CalculationGroup.ToString() : null),
                Description = new  DaxNote(table.Description),
                IsDateTable = (table.DataCategory == Microsoft.AnalysisServices.DimensionType.Time.ToString())
            };
            if (daxTable.IsDateTable == false)
            {
                daxTable.IsDateTable = table.Columns.SingleOrDefault((c) => c.IsKey && c.DataType == Tom.DataType.DateTime) != null;
                if (daxTable.IsDateTable == false)
                {
                    daxTable.IsDateTable = table.Model.Relationships.OfType<Tom.SingleColumnRelationship>().Any((r) =>
                    {
                        return r.IsActive &&
                        (
                            (
                                r.ToTable == table &&
                                r.ToColumn.DataType == Tom.DataType.DateTime &&
                                r.ToCardinality == Tom.RelationshipEndCardinality.One
                            )
                            ||
                            (
                                r.FromTable == table &&
                                r.FromColumn.DataType == Tom.DataType.DateTime &&
                                r.FromCardinality == Tom.RelationshipEndCardinality.One
                            )
                        );
                    });
                }
            }

            foreach (Tom.Column column in table.Columns) {
                AddColumn(daxTable, column);
            }
            foreach (Tom.Measure measure in table.Measures) {
                AddMeasure(daxTable, measure);
            }
            foreach (Tom.Hierarchy hierarchy in table.Hierarchies) {
                AddUserHierarchy(daxTable, hierarchy);
            }

            // Add calculation groups and calculation items
            if (table.CalculationGroup != null) {
                CalculationGroup calcGroup = new(daxTable)
                {
                    Precedence = table.CalculationGroup.Precedence
                };
                foreach (Tom.CalculationItem calcItem in table.CalculationGroup.CalculationItems) {
                    AddCalculationItem(calcGroup, calcItem);
                }
                daxTable.CalculationGroup = calcGroup;

                // Set the first column of the table that is not a RowNumber as a calculation group attribute
                foreach (Column column in daxTable.Columns) {
                    if (!column.IsRowNumber) {
                        column.IsCalculationGroupAttribute = true;
                        break;
                    }
                }
            }

            DaxModel.Tables.Add(daxTable);

        }
        public void AddCalculationItem(CalculationGroup calcGroup, Tom.CalculationItem tomCalcItem)
        {
            Dax.Metadata.CalculationItem calcItem = new(calcGroup)
            {
                ItemExpression = DaxExpression.GetExpression(tomCalcItem.Expression),
                FormatStringDefinition = DaxExpression.GetExpression(tomCalcItem.FormatStringDefinition?.Expression),
                ItemName = new Dax.Metadata.DaxName(tomCalcItem.Name),
                State = tomCalcItem.State.ToString(),
                ErrorMessage = tomCalcItem.ErrorMessage,
                FormatStringState = tomCalcItem.FormatStringDefinition?.State.ToString(),
                FormatStringErrorMessage = tomCalcItem.FormatStringDefinition?.ErrorMessage,
                Description = new DaxNote(tomCalcItem.Description)
            };
            calcGroup.CalculationItems.Add(calcItem);
        }
        public void AddUserHierarchy( Table daxTable, Tom.Hierarchy hierarchy )
        {
            Dax.Metadata.UserHierarchy daxUserHierarchy = new( daxTable )
            {
                HierarchyName = new Dax.Metadata.DaxName(hierarchy.Name),
                IsHidden = hierarchy.IsHidden,
            };
            // Create the hierarchy from the top to the bottom level 
            foreach (Tom.Level level in hierarchy.Levels.OrderBy( t => t.Ordinal ) ) 
            {
                Dax.Metadata.Column levelColumn = daxTable.Columns.Find(t => t.ColumnName.Name == level.Column.Name);
                daxUserHierarchy.Levels.Add(levelColumn);
            }
            daxTable.UserHierarchies.Add(daxUserHierarchy);
        }
        private void AddMeasure(Table daxTable, Tom.Measure measure)
        {
            Dax.Metadata.Measure daxMeasure = new()
            {
                Table = daxTable,
                MeasureName = new Dax.Metadata.DaxName(measure.Name),
                MeasureExpression = Dax.Metadata.DaxExpression.GetExpression(measure?.Expression),
                FormatStringExpression = Dax.Metadata.DaxExpression.GetExpression(measure?.FormatStringDefinition?.Expression),
                DisplayFolder = new DaxNote(measure.DisplayFolder),
                Description = new DaxNote(measure.Description),
                IsHidden = measure.IsHidden,
                DataType = measure.DataType.ToString(),
                DetailRowsExpression = Dax.Metadata.DaxExpression.GetExpression(measure.DetailRowsDefinition?.Expression),
                FormatString = measure.FormatString,
                KpiStatusExpression = Dax.Metadata.DaxExpression.GetExpression(measure.KPI?.StatusExpression),
                KpiTargetExpression = Dax.Metadata.DaxExpression.GetExpression(measure.KPI?.TargetExpression),
                KpiTargetFormatString = measure.KPI?.TargetFormatString,
                KpiTrendExpression = Dax.Metadata.DaxExpression.GetExpression(measure.KPI?.TrendExpression)
            };
            daxTable.Measures.Add(daxMeasure);
        }

        private void AddColumn(Table daxTable, Tom.Column column)
        {
            Dax.Metadata.Column daxColumn = CreateColumn(daxTable, column);
            daxTable.Columns.Add(daxColumn);
        }
        private Dax.Metadata.Column CreateColumn (Table daxTable, Tom.Column column)
        {
            string calculatedColumnExpression =
                (column.Type == Tom.ColumnType.Calculated) ? (column as Tom.CalculatedColumn)?.Expression : null;

            Column col = new Dax.Metadata.Column(daxTable)
            {
                ColumnName = new Dax.Metadata.DaxName(column.Name),
                DataType = column.DataType.ToString(),
                IsHidden = column.IsHidden,
                EncodingHint = column.EncodingHint.ToString(),
                IsAvailableInMDX = column.IsAvailableInMDX,
                IsKey = column.IsKey,
                IsNullable = column.IsNullable,
                IsUnique = column.IsUnique,
                KeepUniqueRows = column.KeepUniqueRows,
                SortByColumnName = new DaxName(column.SortByColumn?.Name),
                IsRowNumber = (column.Type == Tom.ColumnType.RowNumber),
                State = column.State.ToString(),
                ColumnType = column.Type.ToString(),
                ColumnExpression = Dax.Metadata.DaxExpression.GetExpression(calculatedColumnExpression),
                DisplayFolder = new DaxNote(column.DisplayFolder),
                FormatString = column.FormatString,
                Description = new DaxNote(column.Description)
            };

            if (column is Tom.CalculatedTableColumn ctc && !ctc.IsNameInferred) {
                col.IsNameInferred = ctc.IsNameInferred;
                col.SourceColumn = new DaxName(ctc.SourceColumn);
            }

            // if any group by columns exist add them to the list of GroupByColumns
            if (column.RelatedColumnDetails?.GroupByColumns != null) {
                col.GroupByColumns.AddRange(column.RelatedColumnDetails.GroupByColumns.Select(c => new DaxName(c.GroupingColumn.Name)));
            }

            return col;
        }
        public static Dax.Metadata.Model GetDaxModel(Tom.Model model, string extractorApp, string extractorVersion)
        {
            TomExtractor extractor = new(model, extractorApp, extractorVersion);
            return extractor.DaxModel;
        }

        public static Dax.Metadata.Model GetDaxModel(string connectionString, string applicationName, string applicationVersion, bool readStatisticsFromData = true, int sampleRows = 0, bool analyzeDirectQuery = false)
        {
            Tom.Server server = new Tom.Server();
            server.Connect(connectionString);
            var database = GetDatabase(connectionString);
            Tom.Model tomModel = database.Model;
            string databaseName = database.Name;
            string serverName = GetDataSource(connectionString);

            Model daxModel = Dax.Metadata.Extractor.TomExtractor.GetDaxModel(tomModel, applicationName, applicationVersion);

            using (AdomdConnection connection = new(connectionString)) {
                // Populate statistics from DMV
                Dax.Metadata.Extractor.DmvExtractor.PopulateFromDmv(daxModel, connection, serverName, databaseName, applicationName, applicationVersion);

                // Populate statistics by querying the data model
                if (readStatisticsFromData) {
                    Dax.Metadata.Extractor.StatExtractor.UpdateStatisticsModel(daxModel, connection, sampleRows, analyzeDirectQuery);
                }
            }
            return daxModel;
        }

        private static string GetDataSource(string connectionString)
        {
            var builder = new OleDbConnectionStringBuilder(connectionString);
            return builder.DataSource;
        }

        private static string GetInitialCatalog(string connectionString)
        {
            var builder = new OleDbConnectionStringBuilder(connectionString);
            builder.TryGetValue("Initial Catalog", out object initialCatalog);
            return initialCatalog.ToString();
        }
        public static Tom.Database GetDatabase(string serverName, string databaseName)
        {
            Tom.Server server = new();
            server.Connect(serverName);
            Tom.Database db = server.Databases.FindByName(databaseName);
            // if db is null either it does not exist or we do not have admin rights to it
            return db ?? throw new ArgumentException($"The database '{databaseName}' could not be found. Either it does not exist or you do not have admin rights to it.");
        }

        public static Tom.Database GetDatabase(string connectionString)
        {
            Tom.Server server = new();
            server.Connect(connectionString);
            var databaseName = GetInitialCatalog(connectionString);
            Tom.Database db = server.Databases.FindByName(databaseName);
            // if db is null either it does not exist or we do not have admin rights to it
            return db ?? throw new ArgumentException($"The database '{databaseName}' could not be found. Either it does not exist or you do not have admin rights to it.");
        }

        public static Dax.Metadata.Model GetDaxModel(string serverName, string databaseName, string applicationName, string applicationVersion, bool readStatisticsFromData = true, int sampleRows = 0, bool analyzeDirectQuery = false)
        {
            Tom.Database db = GetDatabase(serverName, databaseName);
            Tom.Model tomModel = db.Model;

            Model daxModel = Dax.Metadata.Extractor.TomExtractor.GetDaxModel(tomModel, applicationName, applicationVersion);

            string connectionString = GetConnectionString(serverName, databaseName);

            using (AdomdConnection connection = new(connectionString))
            {
                // Populate statistics from DMV
                Dax.Metadata.Extractor.DmvExtractor.PopulateFromDmv(daxModel, connection, serverName, databaseName, applicationName, applicationVersion);

                // Populate statistics by querying the data model
                if (readStatisticsFromData)
                {
                    Dax.Metadata.Extractor.StatExtractor.UpdateStatisticsModel(daxModel, connection, sampleRows, analyzeDirectQuery );
                }
            }
            return daxModel;
        }

        private static string GetConnectionString(string dataSourceOrConnectionString, string databaseName)
        {
            OleDbConnectionStringBuilder csb = new();
            try
            {
                csb.ConnectionString = dataSourceOrConnectionString;
            }
            catch
            {
                // Assume servername
                csb.Provider = "MSOLAP";
                csb.DataSource = dataSourceOrConnectionString;
            }
            csb["Initial Catalog"] = databaseName;
            return csb.ConnectionString;
        }
    }
}
