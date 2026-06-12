using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data.SQLite;
using System.Diagnostics;
using SX3_SCANER.Helper;
using SX3_SCANER.Model.Respository;

namespace SX3_SCANER.Model
{
    internal class ScanHistoryRepository
    {
        internal const string HistoryTableName = "ScanHistoryView";

        private static string _connectionString;

        public ScanHistoryRepository()
        {
            _connectionString = DatabaseRepository.ConnectionString;
        }

        internal static void CreateTableIfNotExists()
        {
            _connectionString = DatabaseRepository.ConnectionString;
            using (SQLiteConnection connection = DatabaseRepository.CreateConnection())
            {
                string createTableQuery = @"
                    CREATE TABLE IF NOT EXISTS ScanHistoryView (
                        ID INTEGER PRIMARY KEY AUTOINCREMENT,
                        ScanTime DATETIME,
                        BoxName TEXT,
                        ProductPartNumber TEXT,
                        ProductPartName TEXT,
                        SealNo TEXT,
                        LotNo TEXT,
                        ScanData TEXT,
                        ScanResult INTEGER,
                        ScanMessage TEXT,
                        ScanWorker TEXT,
                        BoxType TEXT NOT NULL DEFAULT 'OPEN',
                        IsPartialBox INTEGER NOT NULL DEFAULT 0,
                        BoxDate TEXT,
                        ScanLabelDate TEXT,
                        ActualQty INTEGER NOT NULL DEFAULT 0,
                        TargetQty INTEGER NOT NULL DEFAULT 0
                    );";
                using (SQLiteCommand command = new SQLiteCommand(createTableQuery, connection))
                {
                    command.ExecuteNonQuery();
                }

                EnsureColumn(connection, "BoxType", "TEXT NOT NULL DEFAULT 'OPEN'");
                EnsureColumn(connection, "IsPartialBox", "INTEGER NOT NULL DEFAULT 0");
                EnsureColumn(connection, "BoxDate", "TEXT");
                EnsureColumn(connection, "ScanLabelDate", "TEXT");
                EnsureColumn(connection, "ActualQty", "INTEGER NOT NULL DEFAULT 0");
                EnsureColumn(connection, "TargetQty", "INTEGER NOT NULL DEFAULT 0");
            }
        }

        private static void EnsureColumn(SQLiteConnection connection, string columnName, string definition)
        {
            using (SQLiteCommand command = new SQLiteCommand("PRAGMA table_info(ScanHistoryView)", connection))
            using (SQLiteDataReader reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (string.Equals(reader["name"].ToString(), columnName, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }
            }

            using (SQLiteCommand command = new SQLiteCommand(
                "ALTER TABLE ScanHistoryView ADD COLUMN " + columnName + " " + definition,
                connection))
            {
                command.ExecuteNonQuery();
            }
        }

        public ObservableCollection<ScanHistory> GetAllScanHistory()
        {
            ObservableCollection<ScanHistory> scanHistoryItems = new ObservableCollection<ScanHistory>();
            using (SQLiteConnection connection = DatabaseRepository.CreateConnection())
            {
                string selectQuery = "SELECT * FROM ScanHistoryView";
                using (SQLiteCommand command = new SQLiteCommand(selectQuery, connection))
                {
                    using (SQLiteDataReader reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            scanHistoryItems.Add(ReadScanHistory(reader));
                        }
                    }
                }
            }
            return scanHistoryItems;
        }

        public ObservableCollection<ScanHistory> GetByBoxName(string boxname)
        {
            ObservableCollection<ScanHistory> scanHistoryItems = new ObservableCollection<ScanHistory>();
            using (SQLiteConnection connection = new SQLiteConnection(_connectionString))
            {
                connection.Open();
                string selectQuery = "SELECT * FROM ScanHistoryView WHERE BoxName = @BoxName";
                using (SQLiteCommand command = new SQLiteCommand(selectQuery, connection))
                {
                    command.Parameters.AddWithValue("@BoxName", boxname);
                    using (SQLiteDataReader reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            scanHistoryItems.Add(ReadScanHistory(reader));
                        }
                    }
                }
            }
            return scanHistoryItems;
        }

        public void InsertScanHistory(ScanHistory scanHistory)
        {
            try
            {
                using (SQLiteConnection connection = DatabaseRepository.CreateConnection())
                using (SQLiteTransaction transaction = connection.BeginTransaction())
                {
                    InsertScanHistory(scanHistory, connection, transaction);
                    transaction.Commit();
                }
            }
            catch (Exception ex)
            {
                StartupManager.Log(
                    "Loi insert ScanHistory vao " + DatabaseRepository.DatabasePath +
                    ". Result=" + (scanHistory.ScanResult ? "PASS" : "NG") +
                    ", BoxName=" + scanHistory.BoxName +
                    ", PartNumber=" + scanHistory.ProductPartNumber +
                    ", ScanData=" + scanHistory.ScanData +
                    ". Chi tiet: " + ex);
                throw;
            }
        }

        internal void InsertScanHistory(
            ScanHistory scanHistory,
            SQLiteConnection connection,
            SQLiteTransaction transaction)
        {
            if (scanHistory == null)
                throw new ArgumentNullException(nameof(scanHistory));
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));
            if (transaction == null)
                throw new ArgumentNullException(nameof(transaction));

            const string insertQuery = @"
                INSERT INTO ScanHistoryView (ScanTime, BoxDate, ScanLabelDate, BoxName, ProductPartNumber, ProductPartName, SealNo, LotNo, ScanData, ScanResult, ScanMessage, ScanWorker, BoxType, IsPartialBox, ActualQty, TargetQty)
                VALUES (@ScanTime, @BoxDate, @ScanLabelDate, @BoxName, @ProductPartNumber, @ProductPartName, @SealNo, @LotNo, @ScanData, @ScanResult, @ScanMessage, @ScanWorker, @BoxType, @IsPartialBox, @ActualQty, @TargetQty)";
            using (SQLiteCommand command = new SQLiteCommand(insertQuery, connection, transaction))
            {
                command.Parameters.AddWithValue(
                    "@ScanTime",
                    scanHistory.ScanTime.HasValue
                        ? (object)scanHistory.ScanTime.Value
                        : DBNull.Value);
                command.Parameters.AddWithValue(
                    "@BoxDate",
                    scanHistory.BoxDate.HasValue
                        ? (object)scanHistory.BoxDate.Value.Date
                        : DBNull.Value);
                command.Parameters.AddWithValue(
                    "@ScanLabelDate",
                    scanHistory.ScanLabelDate.HasValue
                        ? (object)scanHistory.ScanLabelDate.Value.Date
                        : DBNull.Value);
                command.Parameters.AddWithValue("@BoxName", scanHistory.BoxName);
                command.Parameters.AddWithValue("@ProductPartNumber", scanHistory.ProductPartNumber);
                command.Parameters.AddWithValue("@ProductPartName", scanHistory.ProductPartName);
                command.Parameters.AddWithValue("@SealNo", scanHistory.SealNo);
                command.Parameters.AddWithValue("@LotNo", scanHistory.LotNo);
                command.Parameters.AddWithValue("@ScanData", scanHistory.ScanData);
                command.Parameters.AddWithValue("@ScanResult", scanHistory.ScanResult ? 1 : 0);
                command.Parameters.AddWithValue("@ScanMessage", scanHistory.ScanMessage);
                command.Parameters.AddWithValue("@ScanWorker", scanHistory.ScanWorker);
                command.Parameters.AddWithValue("@BoxType", scanHistory.BoxType);
                command.Parameters.AddWithValue("@IsPartialBox", scanHistory.IsPartialBox ? 1 : 0);
                command.Parameters.AddWithValue("@ActualQty", scanHistory.ActualQty);
                command.Parameters.AddWithValue("@TargetQty", scanHistory.TargetQty);
                command.ExecuteNonQuery();
            }
        }

        public bool ScanDataExists(string scanData)
        {
            if (string.IsNullOrWhiteSpace(scanData))
                return false;

            using (SQLiteConnection connection = DatabaseRepository.CreateConnection())
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = @"
                    SELECT 1
                    FROM ScanHistoryView
                    WHERE ScanResult = 1
                      AND ScanData = @ScanData COLLATE NOCASE
                    LIMIT 1";
                command.Parameters.AddWithValue("@ScanData", scanData.Trim());
                return command.ExecuteScalar() != null;
            }
        }

        internal static bool IsUniqueScanDataViolation(SQLiteException exception)
        {
            if (exception == null)
                return false;

            string details = exception.ToString();
            return exception.ResultCode == SQLiteErrorCode.Constraint ||
                details.IndexOf("UX_ScanHistoryView_PassScanData", StringComparison.OrdinalIgnoreCase) >= 0 ||
                details.IndexOf("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public void UpdateScanHistory(ScanHistory scanHistory)
        {
            using (SQLiteConnection connection = DatabaseRepository.CreateConnection())
            using (SQLiteTransaction transaction = connection.BeginTransaction())
            {
                string updateQuery = @"
                    UPDATE ScanHistoryView
                    SET ScanTime = @ScanTime, BoxName = @BoxName, ProductPartNumber = @ProductPartNumber, ProductPartName = @ProductPartName, SealNo = @SealNo, LotNo = @LotNo,
                        ScanData = @ScanData, ScanResult = @ScanResult, ScanMessage = @ScanMessage, ScanWorker = @ScanWorker,
                        BoxType = @BoxType, IsPartialBox = @IsPartialBox
                    WHERE ID = @ID";
                using (SQLiteCommand command = new SQLiteCommand(updateQuery, connection, transaction))
                {
                    command.Parameters.AddWithValue(
                        "@ScanTime",
                        scanHistory.ScanTime.HasValue
                            ? (object)scanHistory.ScanTime.Value
                            : DBNull.Value);
                    command.Parameters.AddWithValue("@BoxName", scanHistory.BoxName);
                    command.Parameters.AddWithValue("@ProductPartNumber", scanHistory.ProductPartNumber);
                    command.Parameters.AddWithValue("@ProductPartName", scanHistory.ProductPartName);
                    command.Parameters.AddWithValue("@SealNo", scanHistory.SealNo);
                    command.Parameters.AddWithValue("@LotNo", scanHistory.LotNo);
                    command.Parameters.AddWithValue("@ScanData", scanHistory.ScanData);
                    command.Parameters.AddWithValue("@ScanResult", scanHistory.ScanResult ? 1 : 0);
                    command.Parameters.AddWithValue("@ScanMessage", scanHistory.ScanMessage);
                    command.Parameters.AddWithValue("@ScanWorker", scanHistory.ScanWorker);
                    command.Parameters.AddWithValue("@BoxType", scanHistory.BoxType);
                    command.Parameters.AddWithValue("@IsPartialBox", scanHistory.IsPartialBox ? 1 : 0);
                    command.Parameters.AddWithValue("@ID", scanHistory.ID);
                    command.ExecuteNonQuery();
                }
                transaction.Commit();
            }
        }

        public void DeleteScanHistory(int id)
        {
            using (SQLiteConnection connection = DatabaseRepository.CreateConnection())
            using (SQLiteTransaction transaction = connection.BeginTransaction())
            {
                string deleteQuery = "DELETE FROM ScanHistoryView WHERE ID = @ID";
                using (SQLiteCommand command = new SQLiteCommand(deleteQuery, connection, transaction))
                {
                    command.Parameters.AddWithValue("@ID", id);
                    command.ExecuteNonQuery();
                }
                transaction.Commit();
            }
        }

        public void DeleteByBoxName(string boxName)
        {
            if (string.IsNullOrWhiteSpace(boxName))
            {
                return;
            }

            using (SQLiteConnection connection = DatabaseRepository.CreateConnection())
            using (SQLiteTransaction transaction = connection.BeginTransaction())
            using (SQLiteCommand command = new SQLiteCommand(
                "DELETE FROM ScanHistoryView WHERE BoxName = @BoxName",
                connection,
                transaction))
            {
                command.Parameters.AddWithValue("@BoxName", boxName);
                command.ExecuteNonQuery();
                transaction.Commit();
            }
        }

        public ObservableCollection<ScanHistory> GetNotComplete(string boxname)
        {
            ObservableCollection<ScanHistory> notCompleteScans = new ObservableCollection<ScanHistory>();
            using (SQLiteConnection connection = new SQLiteConnection(_connectionString))
            {
                connection.Open();
                string query = "SELECT * FROM ScanHistoryView WHERE BoxName = @BoxName";
                using (SQLiteCommand command = new SQLiteCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@BoxName", boxname);
                    using (SQLiteDataReader reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            notCompleteScans.Add(ReadScanHistory(reader));
                        }
                    }
                }
            }
            return notCompleteScans;
        }

        public bool CheckExist(string productPartNumber, string sealno, string lotno)
        {
            using (SQLiteConnection connection = new SQLiteConnection(_connectionString))
            {
                connection.Open();
                string query = @"
                    SELECT 1
                    FROM ScanHistoryView
                    WHERE ProductPartNumber = @ProductPartNumber COLLATE NOCASE
                      AND SealNo = @SealNo
                      AND LotNo = @LotNo
                      AND ScanResult = 1
                    LIMIT 1";
                using (SQLiteCommand command = new SQLiteCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@ProductPartNumber", productPartNumber);
                    command.Parameters.AddWithValue("@SealNo", sealno);
                    command.Parameters.AddWithValue("@LotNo", lotno);
                    return command.ExecuteScalar() != null;
                }
            }
        }

        public List<string> GetDistinctSealNos()
        {
            List<string> sealNos = new List<string>() { "All"};
            using (SQLiteConnection connection = new SQLiteConnection(_connectionString))
            {
                connection.Open();
                string query = "SELECT DISTINCT SealNo FROM ScanHistoryView WHERE SealNo IS NOT NULL AND SealNo <> '' ORDER BY SealNo";
                using (SQLiteCommand command = new SQLiteCommand(query, connection))
                {
                    using (SQLiteDataReader reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            string SealNo = reader["SealNo"].ToString();
                            if (!string.IsNullOrEmpty(SealNo))
                            {
                                sealNos.Add(SealNo);
                            }
                        }
                    }
                }
            }
            return sealNos;
        }

        public List<string> GetDistinctProductNumbers()
        {
            List<string> productNumbers = new List<string>() { "All"};
            using (SQLiteConnection connection = new SQLiteConnection(_connectionString))
            {
                connection.Open();
                string query = "SELECT DISTINCT ProductPartNumber FROM ScanHistoryView WHERE ProductPartNumber IS NOT NULL AND ProductPartNumber <> '' ORDER BY ProductPartNumber";
                using (SQLiteCommand command = new SQLiteCommand(query, connection))
                {
                    using (SQLiteDataReader reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            string productNumber = reader["ProductPartNumber"].ToString();
                            if (!string.IsNullOrEmpty(productNumber))
                            {
                                productNumbers.Add(productNumber);
                            }
                        }
                    }
                }
            }
            return productNumbers;
        }

        public List<string> GetDistinctNGMessage()
        {
            List<string> productNumbers = new List<string>() { "All"};
            using (SQLiteConnection connection = new SQLiteConnection(_connectionString))
            {
                connection.Open();
                string query = @"
                    SELECT DISTINCT ScanMessage
                    FROM ScanHistoryView
                    WHERE ScanResult = 0
                      AND ScanMessage IS NOT NULL
                      AND TRIM(ScanMessage) <> ''
                    ORDER BY ScanMessage COLLATE NOCASE";
                using (SQLiteCommand command = new SQLiteCommand(query, connection))
                {
                    using (SQLiteDataReader reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            string scanMessage = reader["ScanMessage"].ToString();
                            if (!string.IsNullOrEmpty(scanMessage) && !productNumbers.Contains(scanMessage))
                            {
                                productNumbers.Add(scanMessage);
                            }
                        }
                    }
                }
            }
            return productNumbers;
        }

        public List<string> GetPartNumberSuggestions(string keyword, int limit = 30)
        {
            List<string> partNumbers = new List<string>();
            int safeLimit = Math.Max(1, Math.Min(limit, 100));
            string searchText = string.IsNullOrWhiteSpace(keyword) ? string.Empty : keyword.Trim();

            using (SQLiteConnection connection = DatabaseRepository.CreateConnection())
            {
                string query;
                if (string.IsNullOrWhiteSpace(searchText))
                {
                    query = @"
                        SELECT ProductPartNumber
                        FROM ScanHistoryView
                        WHERE ProductPartNumber IS NOT NULL AND ProductPartNumber <> ''
                        GROUP BY ProductPartNumber
                        ORDER BY MAX(ID) DESC
                        LIMIT 20";
                }
                else
                {
                    query = @"
                        SELECT DISTINCT ProductPartNumber
                        FROM ScanHistoryView
                        WHERE ProductPartNumber IS NOT NULL
                          AND ProductPartNumber <> ''
                          AND ProductPartNumber COLLATE NOCASE LIKE @Keyword ESCAPE '\'
                        ORDER BY ProductPartNumber
                        LIMIT @Limit";
                }

                using (SQLiteCommand command = new SQLiteCommand(query, connection))
                {
                    if (!string.IsNullOrWhiteSpace(searchText))
                    {
                        command.Parameters.AddWithValue("@Limit", safeLimit);
                        command.Parameters.AddWithValue("@Keyword", "%" + EscapeLikeValue(searchText) + "%");
                    }

                    using (SQLiteDataReader reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            string partNumber = reader["ProductPartNumber"].ToString();
                            if (!string.IsNullOrWhiteSpace(partNumber))
                            {
                                partNumbers.Add(partNumber);
                            }
                        }
                    }
                }
            }

            return partNumbers;
        }



        public ObservableCollection<ScanHistory> SearchHistory(
            string keyword,
            string partNumber,
            string sealNo,
            string scanMessage,
            bool? scanResult,
            int limit = 500)
        {
            return GetScanned(
                partnumber: partNumber,
                sealno: sealNo,
                scandata: keyword,
                scanresult: scanResult,
                scanmessage: scanMessage,
                limit: limit);
        }

        public ObservableCollection<ScanHistory> GetScanned(
            string boxname = null,
            string partnumber = null,
            string sealno = null,
            string scandata = null,
            bool? scanresult = null,
            string scanmessage = null,
            int limit = 500)
        {
            ObservableCollection<ScanHistory> scanHistoryItems = new ObservableCollection<ScanHistory>();

            string normalizedBoxName = NormalizeFilterValue(boxname);
            string normalizedPartNumber = NormalizeFilterValue(partnumber);
            string normalizedSealNo = NormalizeFilterValue(sealno);
            string normalizedScanMessage = NormalizeFilterValue(scanmessage);
            List<string> searchTerms = BuildSearchTerms(scandata);
            int safeLimit = Math.Max(1, Math.Min(limit, 2000));

            using (SQLiteConnection connection = DatabaseRepository.CreateConnection())
            {
                bool hasBoxType = TableHasColumn(
                    connection,
                    HistoryTableName,
                    "BoxType");
                string selectQuery = @"
                    SELECT
                        ID,
                        ScanTime,
                        BoxName,
                        ProductPartNumber,
                        ProductPartName,
                        SealNo,
                        LotNo,
                        ScanData,
                        ScanResult,
                        ScanMessage,
                        ScanWorker,
                        " + SelectColumnOrDefault(
                            connection,
                            HistoryTableName,
                            "BoxType",
                            "'OPEN'") + @",
                        " + SelectColumnOrDefault(
                            connection,
                            HistoryTableName,
                            "IsPartialBox",
                            "0") + @",
                        " + SelectColumnOrDefault(
                            connection,
                            HistoryTableName,
                            "BoxDate",
                            "NULL") + @",
                        " + SelectColumnOrDefault(
                            connection,
                            HistoryTableName,
                            "ScanLabelDate",
                            "NULL") + @",
                        " + SelectColumnOrDefault(
                            connection,
                            HistoryTableName,
                            "ActualQty",
                            "0") + @",
                        " + SelectColumnOrDefault(
                            connection,
                            HistoryTableName,
                            "TargetQty",
                            "0") + @"
                    FROM ScanHistoryView
                    WHERE 1=1";

                if (!string.IsNullOrWhiteSpace(normalizedBoxName))
                {
                    selectQuery += " AND COALESCE(BoxName, '') = @BoxName COLLATE NOCASE";
                }

                if (!string.IsNullOrWhiteSpace(normalizedPartNumber))
                {
                    selectQuery += " AND COALESCE(ProductPartNumber, '') = @ProductPartNumber COLLATE NOCASE";
                }

                if (!string.IsNullOrWhiteSpace(normalizedSealNo))
                {
                    selectQuery += " AND COALESCE(SealNo, '') = @SealNo COLLATE NOCASE";
                }

                if (scanresult.HasValue)
                {
                    selectQuery += " AND ScanResult = @ScanResult";
                }

                if (!string.IsNullOrWhiteSpace(normalizedScanMessage))
                {
                    selectQuery += " AND COALESCE(ScanMessage, '') = @ScanMessage COLLATE NOCASE";
                }

                if (searchTerms.Count > 0)
                {
                    selectQuery += " AND (";
                    for (int i = 0; i < searchTerms.Count; i++)
                    {
                        if (i > 0)
                        {
                            selectQuery += " OR ";
                        }

                        string parameterName = "@Keyword" + i;
                        selectQuery += @"
                            CAST(ID AS TEXT) LIKE " + parameterName + @" ESCAPE '\'
                            OR COALESCE(BoxName, '') COLLATE NOCASE LIKE " + parameterName + @" ESCAPE '\'
                            OR COALESCE(ProductPartNumber, '') COLLATE NOCASE LIKE " + parameterName + @" ESCAPE '\'
                            OR COALESCE(ProductPartName, '') COLLATE NOCASE LIKE " + parameterName + @" ESCAPE '\'
                            OR COALESCE(SealNo, '') COLLATE NOCASE LIKE " + parameterName + @" ESCAPE '\'
                            OR COALESCE(LotNo, '') COLLATE NOCASE LIKE " + parameterName + @" ESCAPE '\'
                            OR COALESCE(ScanMessage, '') COLLATE NOCASE LIKE " + parameterName + @" ESCAPE '\'
                            OR COALESCE(ScanWorker, '') COLLATE NOCASE LIKE " + parameterName + @" ESCAPE '\'
                            OR COALESCE(ScanData, '') COLLATE NOCASE LIKE " + parameterName + @" ESCAPE '\'";
                        if (hasBoxType)
                        {
                            selectQuery += @"
                            OR COALESCE(BoxType, '') COLLATE NOCASE LIKE " + parameterName + @" ESCAPE '\'";
                        }
                    }
                    selectQuery += ")";
                }

                selectQuery += " ORDER BY ID DESC, ScanTime DESC LIMIT @Limit";

                using (SQLiteCommand command = new SQLiteCommand(selectQuery, connection))
                {
                    command.Parameters.AddWithValue("@Limit", safeLimit);

                    if (!string.IsNullOrWhiteSpace(normalizedBoxName))
                        command.Parameters.AddWithValue("@BoxName", normalizedBoxName);
                    if (!string.IsNullOrWhiteSpace(normalizedPartNumber))
                        command.Parameters.AddWithValue("@ProductPartNumber", normalizedPartNumber);
                    if (!string.IsNullOrWhiteSpace(normalizedSealNo))
                        command.Parameters.AddWithValue("@SealNo", normalizedSealNo);
                    if (scanresult.HasValue)
                        command.Parameters.AddWithValue("@ScanResult", scanresult.Value ? 1 : 0);
                    if (!string.IsNullOrWhiteSpace(normalizedScanMessage))
                        command.Parameters.AddWithValue("@ScanMessage", normalizedScanMessage);

                    for (int i = 0; i < searchTerms.Count; i++)
                    {
                        command.Parameters.AddWithValue(
                            "@Keyword" + i,
                            "%" + EscapeLikeValue(searchTerms[i]) + "%");
                    }

                    Debug.WriteLine("Scan history SQL: " + selectQuery);
                    foreach (SQLiteParameter parameter in command.Parameters)
                    {
                        Debug.WriteLine(
                            "Scan history parameter " + parameter.ParameterName +
                            "=" + Convert.ToString(parameter.Value));
                    }

                    using (SQLiteDataReader reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            scanHistoryItems.Add(ReadScanHistory(reader));
                        }
                    }
                }
            }

            RefreshRowIndex(scanHistoryItems);
            return scanHistoryItems;
        }

        private static string NormalizeFilterValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            string trimmed = value.Trim();
            if (string.Equals(trimmed, "All", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, "Tất cả", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, "Tat ca", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return trimmed;
        }

        private static ScanHistory ReadScanHistory(SQLiteDataReader reader)
        {
            return new ScanHistory
            {
                ID = SafeInt(reader, "ID"),
                RowIndex = 0,
                ScanTime = SafeDateTime(reader, "ScanTime"),
                BoxName = SafeString(reader, "BoxName"),
                ProductPartNumber = SafeString(reader, "ProductPartNumber"),
                ProductPartName = SafeString(reader, "ProductPartName"),
                SealNo = SafeString(reader, "SealNo"),
                LotNo = SafeString(reader, "LotNo"),
                ScanData = SafeString(reader, "ScanData"),
                ScanResult = SafeBool(reader, "ScanResult"),
                ScanMessage = SafeString(reader, "ScanMessage"),
                ScanWorker = SafeString(reader, "ScanWorker"),
                BoxType = SafeString(reader, "BoxType", "OPEN"),
                IsPartialBox = SafeBool(reader, "IsPartialBox"),
                BoxDate = SafeDateTime(reader, "BoxDate") ?? SafeDateTime(reader, "ScanTime"),
                ScanLabelDate = SafeDateTime(reader, "ScanLabelDate") ?? ParseSealDate(SafeString(reader, "SealNo")),
                ActualQty = SafeInt(reader, "ActualQty"),
                TargetQty = SafeInt(reader, "TargetQty")
            };
        }

        private static void RefreshRowIndex(ObservableCollection<ScanHistory> items)
        {
            if (items == null) return;
            for (int i = 0; i < items.Count; i++)
            {
                items[i].RowIndex = i + 1;
            }
        }

        private static string SafeString(SQLiteDataReader reader, string columnName, string defaultValue = "")
        {
            int ordinal;
            if (!TryGetOrdinal(reader, columnName, out ordinal) ||
                reader.IsDBNull(ordinal))
            {
                return defaultValue;
            }

            object value = reader.GetValue(ordinal);
            return value == null || value == DBNull.Value ? defaultValue : Convert.ToString(value);
        }

        private static int SafeInt(SQLiteDataReader reader, string columnName)
        {
            int ordinal;
            if (!TryGetOrdinal(reader, columnName, out ordinal) ||
                reader.IsDBNull(ordinal))
            {
                return 0;
            }

            object value = reader.GetValue(ordinal);
            int result;
            return int.TryParse(Convert.ToString(value), out result) ? result : 0;
        }

        private static bool SafeBool(SQLiteDataReader reader, string columnName)
        {
            int ordinal;
            if (!TryGetOrdinal(reader, columnName, out ordinal) ||
                reader.IsDBNull(ordinal))
            {
                return false;
            }

            object value = reader.GetValue(ordinal);
            if (value is bool) return (bool)value;

            int numericValue;
            if (int.TryParse(Convert.ToString(value), out numericValue))
            {
                return numericValue != 0;
            }

            bool boolValue;
            return bool.TryParse(Convert.ToString(value), out boolValue) && boolValue;
        }

        private static DateTime? SafeDateTime(SQLiteDataReader reader, string columnName)
        {
            int ordinal;
            if (!TryGetOrdinal(reader, columnName, out ordinal) ||
                reader.IsDBNull(ordinal))
            {
                return null;
            }

            object value = reader.GetValue(ordinal);
            DateTime result;
            return DateTime.TryParse(Convert.ToString(value), out result)
                ? (DateTime?)result
                : null;
        }

        private static bool HasColumn(SQLiteDataReader reader, string columnName)
        {
            int ordinal;
            return TryGetOrdinal(reader, columnName, out ordinal);
        }

        private static string SelectColumnOrDefault(
            SQLiteConnection connection,
            string tableName,
            string columnName,
            string defaultSql)
        {
            return TableHasColumn(connection, tableName, columnName)
                ? columnName
                : defaultSql + " AS " + columnName;
        }

        private static bool TableHasColumn(
            SQLiteConnection connection,
            string tableName,
            string columnName)
        {
            using (SQLiteCommand command = new SQLiteCommand(
                "PRAGMA table_info(" + tableName + ")",
                connection))
            using (SQLiteDataReader reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (string.Equals(
                        SafeString(reader, "name"),
                        columnName,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TryGetOrdinal(
            SQLiteDataReader reader,
            string columnName,
            out int ordinal)
        {
            ordinal = -1;
            if (reader == null || string.IsNullOrWhiteSpace(columnName))
                return false;

            try
            {
                ordinal = reader.GetOrdinal(columnName);
                return ordinal >= 0;
            }
            catch (IndexOutOfRangeException)
            {
            }
            catch (ArgumentException)
            {
            }

            for (int i = 0; i < reader.FieldCount; i++)
            {
                if (string.Equals(reader.GetName(i), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    ordinal = i;
                    return true;
                }
            }

            return false;
        }

        private static DateTime? ParseSealDate(string sealNo)
        {
            DateTime value;
            return DateTime.TryParseExact(
                sealNo,
                "yyMMdd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out value)
                    ? (DateTime?)value.Date
                    : null;
        }

        private static List<string> BuildSearchTerms(string keyword)
        {
            var terms = new List<string>();
            if (string.IsNullOrWhiteSpace(keyword) || keyword.Trim() == "All")
                return terms;

            string original = keyword.Trim();
            AddUnique(terms, original);
            AddUnique(terms, original.ToUpperInvariant());
            AddUnique(terms, original.ToLowerInvariant());
            string normalized = NormalizeSearchText(keyword);
            AddUnique(terms, normalized);

            if (normalized == "PASS" || normalized == "OK")
            {
                AddUnique(terms, "PASS");
                AddUnique(terms, "OK");
            }
            else if (normalized == "NG")
            {
                AddUnique(terms, "NG");
            }
            else if (normalized.Contains("SAI DAI"))
            {
                AddUnique(terms, "NG - Sai \u0111\u1ED9 d\u00E0i");
                AddUnique(terms, "Sai d\u00E0i");
                AddUnique(terms, "Sai dai");
                AddUnique(terms, "LEN");
            }
            else if (normalized.Contains("SAI DAU"))
            {
                AddUnique(terms, "NG - Sai \u0111\u1EA7u m\u00E3 / Prefix");
                AddUnique(terms, "Sai \u0111\u1EA7u");
                AddUnique(terms, "Sai dau");
                AddUnique(terms, "Prefix");
                AddUnique(terms, "PFX");
            }
            else if (normalized.Contains("SAI MA") ||
                     normalized.Contains("TEN SAN PHAM"))
            {
                AddUnique(terms, "NG - Sai m\u00E3 s\u1EA3n ph\u1EA9m / PartName");
                AddUnique(terms, "Lỗi tên sản phẩm");
                AddUnique(terms, "LỖI TÊN SẢN PHẨM");
                AddUnique(terms, "Sai m\u00E3");
                AddUnique(terms, "Sai tên sản phẩm");
                AddUnique(terms, "Sai ma");
                AddUnique(terms, "PartName");
                AddUnique(terms, "PNAME");
            }
            else if ((normalized.Contains("TRUNG NGAY") ||
                      normalized.Contains("TRUNG SEAL") ||
                      normalized.Contains("DUP DATE")))
            {
                AddUnique(terms, "NG - Trùng ngày / SealNo");
                AddUnique(terms, "Trùng ngày");
                AddUnique(terms, "Trung ngay");
                AddUnique(terms, "Trùng SealNo");
                AddUnique(terms, "DUP_DATE");
            }
            else if (normalized.Contains("SAI NGAY") || normalized.Contains("SAI SEAL") || normalized.Contains("SAI DATE"))
            {
                AddUnique(terms, "NG - Sai ng\u00E0y/SealNo");
                AddUnique(terms, "Sai ng\u00E0y");
                AddUnique(terms, "Sai ngay");
                AddUnique(terms, "Sai seal");
                AddUnique(terms, "Sai date");
                AddUnique(terms, "DATE");
            }
            else if (normalized.Contains("SAI LOT"))
            {
                AddUnique(terms, "NG - Sai LotNo");
                AddUnique(terms, "Sai lot");
                AddUnique(terms, "LOT");
            }
            else if (normalized.Contains("TRUNG"))
            {
                AddUnique(terms, "NG - Tr\u00F9ng LotNo");
                AddUnique(terms, "Tr\u00F9ng");
                AddUnique(terms, "Trung");
                AddUnique(terms, "DUP");
            }
            else if (normalized.Contains("THUNG LE"))
            {
                AddUnique(terms, "PARTIAL");
                AddUnique(terms, "TH\u00D9NG L\u1EBA");
                AddUnique(terms, "Th\u00F9ng l\u1EBB");
                AddUnique(terms, "Thung le");
            }
            else if (normalized.Contains("THUNG DU"))
            {
                AddUnique(terms, "FULL");
                AddUnique(terms, "TH\u00D9NG \u0110\u1EE6");
                AddUnique(terms, "Th\u00F9ng \u0111\u1EE7");
                AddUnique(terms, "Thung du");
            }

            return terms;
        }

        private static string NormalizeSearchText(string value)
        {
            return ScanHistory.RemoveVietnameseSigns(value ?? string.Empty).Trim().ToUpperInvariant();
        }

        private static void AddUnique(List<string> terms, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;

            string trimmed = value.Trim();
            foreach (string term in terms)
            {
                if (string.Equals(term, trimmed, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            terms.Add(trimmed);
        }

        private static string EscapeLikeValue(string value)
        {
            return value
                .Replace(@"\", @"\\")
                .Replace("%", @"\%")
                .Replace("_", @"\_");
        }
        public void UpdateWorkerByBoxName(string boxName, string worker)
        {
            if (string.IsNullOrWhiteSpace(boxName) || string.IsNullOrWhiteSpace(worker))
                return;

            using (SQLiteConnection conn = DatabaseRepository.CreateConnection())
            using (SQLiteTransaction transaction = conn.BeginTransaction())
            {
                UpdateWorkerByBoxName(boxName, worker, conn, transaction);
                transaction.Commit();
            }
        }

        internal void UpdateWorkerByBoxName(
            string boxName,
            string worker,
            SQLiteConnection connection,
            SQLiteTransaction transaction)
        {
            if (string.IsNullOrWhiteSpace(boxName) || string.IsNullOrWhiteSpace(worker))
                return;

            const string sql = @"
                UPDATE ScanHistoryView
                SET ScanWorker = @ScanWorker
                WHERE BoxName = @BoxName";
            using (SQLiteCommand command = new SQLiteCommand(sql, connection, transaction))
            {
                command.Parameters.AddWithValue("@ScanWorker", worker);
                command.Parameters.AddWithValue("@BoxName", boxName);
                command.ExecuteNonQuery();
            }
        }

        public void SetBoxTypeByBoxName(string boxName, bool isPartial)
        {
            if (string.IsNullOrWhiteSpace(boxName))
                return;

            using (SQLiteConnection conn = DatabaseRepository.CreateConnection())
            using (SQLiteTransaction transaction = conn.BeginTransaction())
            {
                SetBoxTypeByBoxName(boxName, isPartial, conn, transaction);
                transaction.Commit();
            }
        }

        internal void SetBoxTypeByBoxName(
            string boxName,
            bool isPartial,
            SQLiteConnection connection,
            SQLiteTransaction transaction)
        {
            if (string.IsNullOrWhiteSpace(boxName))
                return;

            const string sql = @"
                UPDATE ScanHistoryView
                SET BoxType = @BoxType,
                    IsPartialBox = @IsPartialBox,
                    ActualQty = @ActualQty,
                    TargetQty = CASE WHEN TargetQty > 0 THEN TargetQty ELSE @TargetQty END
                WHERE BoxName = @BoxName";
            using (SQLiteCommand command = new SQLiteCommand(sql, connection, transaction))
            {
                command.Parameters.AddWithValue("@BoxType", isPartial ? "PARTIAL" : "FULL");
                command.Parameters.AddWithValue("@IsPartialBox", isPartial ? 1 : 0);
                command.Parameters.AddWithValue("@ActualQty", GetPassCount(boxName, connection, transaction));
                command.Parameters.AddWithValue("@TargetQty", GetTargetQty(boxName, connection, transaction));
                command.Parameters.AddWithValue("@BoxName", boxName);
                command.ExecuteNonQuery();
            }
        }

        private static int GetPassCount(
            string boxName,
            SQLiteConnection connection,
            SQLiteTransaction transaction)
        {
            using (SQLiteCommand command = new SQLiteCommand(
                "SELECT COUNT(1) FROM ScanHistoryView WHERE BoxName = @BoxName AND ScanResult = 1",
                connection,
                transaction))
            {
                command.Parameters.AddWithValue("@BoxName", boxName);
                return Convert.ToInt32(command.ExecuteScalar());
            }
        }

        private static int GetTargetQty(
            string boxName,
            SQLiteConnection connection,
            SQLiteTransaction transaction)
        {
            using (SQLiteCommand command = new SQLiteCommand(
                "SELECT COALESCE(MAX(BoxQuantity), 0) FROM BoxProduct WHERE BoxName = @BoxName",
                connection,
                transaction))
            {
                command.Parameters.AddWithValue("@BoxName", boxName);
                return Convert.ToInt32(command.ExecuteScalar());
            }
        }

        internal void CancelBoxByBoxName(
            string boxName,
            string worker,
            SQLiteConnection connection,
            SQLiteTransaction transaction)
        {
            if (string.IsNullOrWhiteSpace(boxName))
                return;

            const string sql = @"
                UPDATE ScanHistoryView
                SET BoxType = 'CANCELLED',
                    IsPartialBox = 0,
                    ScanWorker = CASE
                        WHEN @ScanWorker = '' THEN ScanWorker
                        ELSE @ScanWorker
                    END
                WHERE BoxName = @BoxName";
            using (SQLiteCommand command = new SQLiteCommand(sql, connection, transaction))
            {
                command.Parameters.AddWithValue("@ScanWorker", (worker ?? string.Empty).Trim());
                command.Parameters.AddWithValue("@BoxName", boxName);
                command.ExecuteNonQuery();
            }
        }
    }
}
