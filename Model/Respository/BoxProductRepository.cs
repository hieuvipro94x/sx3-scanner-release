using System;
using System.Collections.ObjectModel;
using System.Data.SQLite;
using SX3_SCANER.Helper;
using SX3_SCANER.Model.Respository;

namespace SX3_SCANER.Model
{
    internal class BoxProductRepository
    {
        private static string _connectionString = DatabaseRepository.ConnectionString;

        public BoxProductRepository()
        {
            _connectionString = DatabaseRepository.ConnectionString;
        }

        public static void CreateTableIfNotExists()
        {
            _connectionString = DatabaseRepository.ConnectionString;

            using (SQLiteConnection connection = DatabaseRepository.CreateConnection())
            {
                string createTableQuery = @"
                    CREATE TABLE IF NOT EXISTS BoxProduct (
                        ID INTEGER PRIMARY KEY AUTOINCREMENT,
                        BoxName TEXT,
                        ProductPartName TEXT,
                        ProductPartNumber TEXT,
                        BoxSealNo TEXT,
                        BoxQuantity INTEGER,
                        BoxProgress INTEGER,
                        BoxComplete INTEGER,
                        BoxWorker TEXT,
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

                using (SQLiteCommand command = new SQLiteCommand(
                    "UPDATE BoxProduct SET BoxType = CASE WHEN BoxComplete = 1 THEN 'FULL' ELSE 'OPEN' END WHERE BoxType IS NULL OR BoxType = '' OR (BoxComplete = 1 AND BoxType = 'OPEN')",
                    connection))
                {
                    command.ExecuteNonQuery();
                }
            }
        }

        private static void EnsureColumn(SQLiteConnection connection, string columnName, string definition)
        {
            using (SQLiteCommand command = new SQLiteCommand("PRAGMA table_info(BoxProduct)", connection))
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
                "ALTER TABLE BoxProduct ADD COLUMN " + columnName + " " + definition,
                connection))
            {
                command.ExecuteNonQuery();
            }
        }

        public ObservableCollection<BoxProduct> GetAllBoxProducts()
        {
            ObservableCollection<BoxProduct> boxProducts = new ObservableCollection<BoxProduct>();
            using (SQLiteConnection connection = new SQLiteConnection(_connectionString))
            {
                connection.Open();
                string selectQuery = "SELECT * FROM BoxProduct";
                using (SQLiteCommand command = new SQLiteCommand(selectQuery, connection))
                {
                    using (SQLiteDataReader reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            boxProducts.Add(new BoxProduct
                            {
                                ID = Convert.ToInt32(reader["ID"]),
                                BoxName = reader["BoxName"].ToString(),
                                ProductPartName = reader["ProductPartName"].ToString(),
                                ProductPartNumber = reader["ProductPartNumber"].ToString(),
                                BoxSealNo = reader["BoxSealNo"].ToString(),
                                BoxQuantity = Convert.ToInt32(reader["BoxQuantity"]),
                                BoxProgress = Convert.ToInt32(reader["BoxProgress"]),
                                BoxComplete = Convert.ToBoolean(Convert.ToInt32(reader["BoxComplete"])),
                                BoxWorker = reader["BoxWorker"].ToString(),
                                BoxType = reader["BoxType"].ToString(),
                                IsPartialBox = Convert.ToBoolean(Convert.ToInt32(reader["IsPartialBox"])),
                                BoxDate = ReadDate(reader, "BoxDate", reader["BoxSealNo"].ToString()),
                                ScanLabelDate = ReadDate(reader, "ScanLabelDate", reader["BoxSealNo"].ToString()),
                                ActualQty = Convert.ToInt32(reader["ActualQty"]),
                                TargetQty = Convert.ToInt32(reader["TargetQty"])
                            });
                        }
                    }
                }
            }
            return boxProducts;
        }

        public void InsertBoxProduct(BoxProduct boxProduct)
        {
            try
            {
                using (SQLiteConnection connection = DatabaseRepository.CreateConnection())
                using (SQLiteTransaction transaction = connection.BeginTransaction())
                {
                    InsertBoxProduct(boxProduct, connection, transaction);
                    transaction.Commit();
                }
            }
            catch (Exception ex)
            {
                StartupManager.Log("Loi insert BoxProduct vao " + DatabaseRepository.DatabasePath +
                    ". BoxName=" + (boxProduct != null ? boxProduct.BoxName : string.Empty) + ". Chi tiet: " + ex);
                throw;
            }
        }

        internal void InsertBoxProduct(
            BoxProduct boxProduct,
            SQLiteConnection connection,
            SQLiteTransaction transaction)
        {
            if (boxProduct == null)
                throw new ArgumentNullException(nameof(boxProduct));

            const string insertQuery = @"
                INSERT INTO BoxProduct (BoxName, ProductPartName, ProductPartNumber, BoxSealNo, BoxQuantity, BoxProgress, BoxComplete, BoxWorker, BoxType, IsPartialBox, BoxDate, ScanLabelDate, ActualQty, TargetQty)
                VALUES (@BoxName, @ProductPartName, @ProductPartNumber, @BoxSealNo, @BoxQuantity, @BoxProgress, @BoxComplete, @BoxWorker, @BoxType, @IsPartialBox, @BoxDate, @ScanLabelDate, @ActualQty, @TargetQty)";
            using (SQLiteCommand command = new SQLiteCommand(insertQuery, connection, transaction))
            {
                command.Parameters.AddWithValue("@BoxName", boxProduct.BoxName);
                command.Parameters.AddWithValue("@ProductPartName", boxProduct.ProductPartName);
                command.Parameters.AddWithValue("@ProductPartNumber", boxProduct.ProductPartNumber);
                command.Parameters.AddWithValue("@BoxSealNo", boxProduct.BoxSealNo);
                command.Parameters.AddWithValue("@BoxQuantity", boxProduct.BoxQuantity);
                command.Parameters.AddWithValue("@BoxProgress", boxProduct.BoxProgress);
                command.Parameters.AddWithValue("@BoxComplete", boxProduct.BoxComplete ? 1 : 0);
                command.Parameters.AddWithValue("@BoxWorker", boxProduct.BoxWorker);
                command.Parameters.AddWithValue("@BoxType", boxProduct.BoxType);
                command.Parameters.AddWithValue("@IsPartialBox", boxProduct.IsPartialBox ? 1 : 0);
                command.Parameters.AddWithValue("@BoxDate", boxProduct.BoxDate.HasValue ? (object)boxProduct.BoxDate.Value.Date : DBNull.Value);
                command.Parameters.AddWithValue("@ScanLabelDate", boxProduct.ScanLabelDate.HasValue ? (object)boxProduct.ScanLabelDate.Value.Date : DBNull.Value);
                command.Parameters.AddWithValue("@ActualQty", boxProduct.ActualQty);
                command.Parameters.AddWithValue("@TargetQty", boxProduct.TargetQty);
                command.ExecuteNonQuery();
            }
        }

        public void UpdateBoxProduct(BoxProduct boxProduct)
        {
            try
            {
                using (SQLiteConnection connection = DatabaseRepository.CreateConnection())
                using (SQLiteTransaction transaction = connection.BeginTransaction())
                {
                    string updateQuery = @"
                        UPDATE BoxProduct
                        SET BoxName = @BoxName, ProductPartName = @ProductPartName, ProductPartNumber = @ProductPartNumber, BoxSealNo = @BoxSealNo,
                            BoxQuantity = @BoxQuantity, BoxProgress = @BoxProgress, BoxComplete = @BoxComplete, BoxWorker = @BoxWorker,
                            BoxType = @BoxType, IsPartialBox = @IsPartialBox, BoxDate = @BoxDate,
                            ScanLabelDate = @ScanLabelDate, ActualQty = @ActualQty, TargetQty = @TargetQty
                        WHERE ID = @ID";
                    using (SQLiteCommand command = new SQLiteCommand(updateQuery, connection, transaction))
                    {
                        command.Parameters.AddWithValue("@BoxName", boxProduct.BoxName);
                        command.Parameters.AddWithValue("@ProductPartName", boxProduct.ProductPartName);
                        command.Parameters.AddWithValue("@ProductPartNumber", boxProduct.ProductPartNumber);
                        command.Parameters.AddWithValue("@BoxSealNo", boxProduct.BoxSealNo);
                        command.Parameters.AddWithValue("@BoxQuantity", boxProduct.BoxQuantity);
                        command.Parameters.AddWithValue("@BoxProgress", boxProduct.BoxProgress);
                        command.Parameters.AddWithValue("@BoxComplete", boxProduct.BoxComplete ? 1 : 0);
                        command.Parameters.AddWithValue("@BoxWorker", boxProduct.BoxWorker);
                        command.Parameters.AddWithValue("@BoxType", boxProduct.BoxType);
                        command.Parameters.AddWithValue("@IsPartialBox", boxProduct.IsPartialBox ? 1 : 0);
                        command.Parameters.AddWithValue("@BoxDate", boxProduct.BoxDate.HasValue ? (object)boxProduct.BoxDate.Value.Date : DBNull.Value);
                        command.Parameters.AddWithValue("@ScanLabelDate", boxProduct.ScanLabelDate.HasValue ? (object)boxProduct.ScanLabelDate.Value.Date : DBNull.Value);
                        command.Parameters.AddWithValue("@ActualQty", boxProduct.ActualQty);
                        command.Parameters.AddWithValue("@TargetQty", boxProduct.TargetQty);
                        command.Parameters.AddWithValue("@ID", boxProduct.ID);
                        command.ExecuteNonQuery();
                    }

                    transaction.Commit();
                }
            }
            catch (Exception ex)
            {
                StartupManager.Log("Loi update BoxProduct trong " + DatabaseRepository.DatabasePath +
                    ". ID=" + (boxProduct != null ? boxProduct.ID.ToString() : string.Empty) + ". Chi tiet: " + ex);
                throw;
            }
        }

        public void DeleteBoxProduct(int id)
        {
            using (SQLiteConnection connection = DatabaseRepository.CreateConnection())
            using (SQLiteTransaction transaction = connection.BeginTransaction())
            {
                string deleteQuery = "DELETE FROM BoxProduct WHERE ID = @ID";
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
                "DELETE FROM BoxProduct WHERE BoxName = @BoxName AND BoxComplete = 0",
                connection,
                transaction))
            {
                command.Parameters.AddWithValue("@BoxName", boxName);
                command.ExecuteNonQuery();
                transaction.Commit();
            }
        }

        public string GetNotComplete(string partnumber, DateTime date)
        {
            using (SQLiteConnection connection = new SQLiteConnection(_connectionString))
            {
                connection.Open();
                string query = @"
                    SELECT BoxName
                    FROM BoxProduct
                    WHERE ProductPartNumber = @PartNumber
                      AND BoxComplete = 0
                      AND BoxSealNo = @BoxSealNo
                      AND COALESCE(BoxType, 'OPEN') = 'OPEN'
                    ORDER BY ID DESC
                    LIMIT 1";
                using (SQLiteCommand command = new SQLiteCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@PartNumber", partnumber);
                    command.Parameters.AddWithValue("@BoxSealNo", date.ToString("yyMMdd"));
                    object result = command.ExecuteScalar();

                    if (result != null && result != DBNull.Value)
                    {
                        return result.ToString();
                    }
                    else
                    {
                        return null; // Trả về null nếu không tìm thấy
                    }
                }
            }
        }

        public DateTime? GetBoxCreatedDate(string boxName)
        {
            if (string.IsNullOrWhiteSpace(boxName))
            {
                return null;
            }

            using (SQLiteConnection connection =
                DatabaseRepository.CreateConnection())
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = @"
                    SELECT BoxSealNo
                    FROM BoxProduct
                    WHERE BoxName = @BoxName
                    ORDER BY ID DESC
                    LIMIT 1";
                command.Parameters.AddWithValue("@BoxName", boxName);

                string boxSealNo = Convert.ToString(command.ExecuteScalar());
                if (DateTime.TryParseExact(
                    boxSealNo,
                    "yyMMdd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None,
                    out DateTime boxCreatedDate))
                {
                    return boxCreatedDate.Date;
                }
            }

            return null;
        }

        public string GetLatestNotComplete(string partNumber)
        {
            if (string.IsNullOrWhiteSpace(partNumber))
                return null;

            using (SQLiteConnection connection = DatabaseRepository.CreateConnection())
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = @"
                    SELECT BoxName
                    FROM BoxProduct
                    WHERE ProductPartNumber = @PartNumber COLLATE NOCASE
                      AND BoxComplete = 0
                      AND COALESCE(BoxType, 'OPEN') = 'OPEN'
                    ORDER BY ID DESC
                    LIMIT 1";
                command.Parameters.AddWithValue("@PartNumber", partNumber.Trim());
                return Convert.ToString(command.ExecuteScalar());
            }
        }

        public void UpdateScanLabelDate(string boxName, DateTime scanLabelDate)
        {
            if (string.IsNullOrWhiteSpace(boxName))
                return;

            using (SQLiteConnection connection = DatabaseRepository.CreateConnection())
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = @"
                    UPDATE BoxProduct
                    SET ScanLabelDate = @ScanLabelDate
                    WHERE BoxName = @BoxName
                      AND BoxComplete = 0";
                command.Parameters.AddWithValue("@ScanLabelDate", scanLabelDate.Date);
                command.Parameters.AddWithValue("@BoxName", boxName);
                command.ExecuteNonQuery();
            }
        }

        public string GetNextBoxName(DateTime businessDate)
        {
            string today = businessDate.ToString("yyMMdd");
            int nextBoxNumber = GetNextBoxNumberForToday(today);
            string nextBoxName = $"P{today}{nextBoxNumber.ToString().PadLeft(4, '0')}";
            return nextBoxName;
        }

        private int GetNextBoxNumberForToday(string today)
        {
            using (SQLiteConnection connection = new SQLiteConnection(_connectionString))
            {
                connection.Open();
                string query = @"
                    SELECT BoxName
                    FROM (
                        SELECT BoxName FROM BoxProduct WHERE BoxSealNo = @Today
                        UNION ALL
                        SELECT BoxName FROM ScanHistoryView WHERE BoxName LIKE @TodayPrefix
                    )
                    ORDER BY BoxName DESC
                    LIMIT 1";
                using (SQLiteCommand command = new SQLiteCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@Today", today);
                    command.Parameters.AddWithValue("@TodayPrefix", "P" + today + "%");
                    object result = command.ExecuteScalar();

                    if (result != null && result != DBNull.Value)
                    {
                        string lastBoxName = result.ToString();
                        if (lastBoxName.Length >= 10 && lastBoxName.StartsWith($"P{today}"))
                        {
                            string lastBoxNumberString = lastBoxName.Substring(lastBoxName.Length -4);
                            if (int.TryParse(lastBoxNumberString, out int lastBoxNumber))
                            {
                                return lastBoxNumber + 1;
                            }
                        }
                    }
                    return 1; // Trả về 1 nếu không tìm thấy hoặc lỗi
                }
            }
        }

        public ObservableCollection<BoxProduct> GetAllTodayBox()
        {
            return GetAllTodayBox(DateTime.Today);
        }

        public ObservableCollection<BoxProduct> GetAllTodayBox(DateTime businessDate)
        {
            ObservableCollection<BoxProduct> todayBoxes = new ObservableCollection<BoxProduct>();
            DateTime date = businessDate.Date;
            string sealNo = date.ToString("yyMMdd");

            using (SQLiteConnection connection = DatabaseRepository.CreateConnection())
            {
                const string query = @"
                    SELECT *
                    FROM BoxProduct
                    WHERE date(BoxDate) = date(@BusinessDate)
                       OR (
                            (BoxDate IS NULL OR TRIM(BoxDate) = '')
                            AND (
                                BoxSealNo = @SealNo
                                OR BoxName LIKE @TodayPrefix
                            )
                       )
                    ORDER BY ID DESC";
                using (SQLiteCommand command = new SQLiteCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@BusinessDate", date);
                    command.Parameters.AddWithValue("@SealNo", sealNo);
                    command.Parameters.AddWithValue("@TodayPrefix", "P" + sealNo + "%");
                    using (SQLiteDataReader reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            todayBoxes.Add(new BoxProduct
                            {
                                ID = Convert.ToInt32(reader["ID"]),
                                BoxName = reader["BoxName"].ToString(),
                                ProductPartName = reader["ProductPartName"].ToString(),
                                ProductPartNumber = reader["ProductPartNumber"].ToString(),
                                BoxSealNo = reader["BoxSealNo"].ToString(),
                                BoxQuantity = Convert.ToInt32(reader["BoxQuantity"]),
                                BoxProgress = Convert.ToInt32(reader["BoxProgress"]),
                                BoxComplete = Convert.ToBoolean(Convert.ToInt32(reader["BoxComplete"])),
                                BoxWorker = reader["BoxWorker"].ToString(),
                                BoxType = reader["BoxType"].ToString(),
                                IsPartialBox = Convert.ToBoolean(Convert.ToInt32(reader["IsPartialBox"])),
                                BoxDate = ReadDate(reader, "BoxDate", reader["BoxSealNo"].ToString()),
                                ScanLabelDate = ReadDate(reader, "ScanLabelDate", reader["BoxSealNo"].ToString()),
                                ActualQty = Convert.ToInt32(reader["ActualQty"]),
                                TargetQty = Convert.ToInt32(reader["TargetQty"])
                            });
                        }
                    }
                }
            }
            return todayBoxes;
        }

        public void UpdateBoxProgress(string boxname)
        {
            using (SQLiteConnection connection = DatabaseRepository.CreateConnection())
            using (SQLiteTransaction transaction = connection.BeginTransaction())
            {
                UpdateBoxProgress(boxname, null, connection, transaction);
                transaction.Commit();
            }
        }

        internal void UpdateBoxProgress(
            string boxName,
            DateTime? scanLabelDate,
            SQLiteConnection connection,
            SQLiteTransaction transaction)
        {
            const string updateQuery = @"
                UPDATE BoxProduct
                SET BoxProgress = BoxProgress + 1
                    , ActualQty = ActualQty + 1
                    , ScanLabelDate = COALESCE(@ScanLabelDate, ScanLabelDate)
                WHERE BoxName = @BoxName
                  AND BoxComplete = 0
                  AND COALESCE(BoxType, 'OPEN') = 'OPEN'";
            using (SQLiteCommand command = new SQLiteCommand(updateQuery, connection, transaction))
            {
                command.Parameters.AddWithValue("@BoxName", boxName);
                command.Parameters.AddWithValue(
                    "@ScanLabelDate",
                    scanLabelDate.HasValue
                        ? (object)scanLabelDate.Value.Date
                        : DBNull.Value);
                if (command.ExecuteNonQuery() == 0)
                {
                    throw new InvalidOperationException(
                        "Khong tim thay thung dang mo de cap nhat tien do: " + boxName);
                }
            }
        }

        public void SetBoxComplete(string boxname, bool isPartial, string worker)
        {
            using (SQLiteConnection connection = DatabaseRepository.CreateConnection())
            using (SQLiteTransaction transaction = connection.BeginTransaction())
            {
                SetBoxComplete(boxname, isPartial, worker, connection, transaction);
                transaction.Commit();
            }
        }

        internal void SetBoxComplete(
            string boxName,
            bool isPartial,
            string worker,
            SQLiteConnection connection,
            SQLiteTransaction transaction)
        {
            const string updateQuery = @"
                UPDATE BoxProduct
                SET BoxComplete = 1,
                    BoxType = @BoxType,
                    IsPartialBox = @IsPartialBox,
                    BoxWorker = @BoxWorker,
                    ActualQty = BoxProgress,
                    TargetQty = CASE WHEN TargetQty > 0 THEN TargetQty ELSE BoxQuantity END
                WHERE BoxName = @BoxName AND BoxComplete = 0";
            using (SQLiteCommand command = new SQLiteCommand(updateQuery, connection, transaction))
            {
                command.Parameters.AddWithValue("@BoxName", boxName);
                command.Parameters.AddWithValue("@BoxType", isPartial ? "PARTIAL" : "FULL");
                command.Parameters.AddWithValue("@IsPartialBox", isPartial ? 1 : 0);
                command.Parameters.AddWithValue("@BoxWorker", worker ?? string.Empty);
                if (command.ExecuteNonQuery() == 0)
                {
                    throw new InvalidOperationException(
                        "Khong tim thay thung dang mo de hoan tat: " + boxName);
                }
            }
        }

        internal void CancelBox(
            string boxName,
            string worker,
            SQLiteConnection connection,
            SQLiteTransaction transaction)
        {
            const string updateQuery = @"
                UPDATE BoxProduct
                SET BoxComplete = 1,
                    BoxType = 'CANCELLED',
                    IsPartialBox = 0,
                    BoxWorker = @BoxWorker
                WHERE BoxName = @BoxName
                  AND BoxComplete = 0";
            using (SQLiteCommand command = new SQLiteCommand(
                updateQuery,
                connection,
                transaction))
            {
                command.Parameters.AddWithValue("@BoxName", boxName);
                command.Parameters.AddWithValue(
                    "@BoxWorker",
                    (worker ?? string.Empty).Trim());
                command.ExecuteNonQuery();
            }
        }

        private static DateTime? ReadDate(
            SQLiteDataReader reader,
            string columnName,
            string fallbackSealNo)
        {
            DateTime value;
            string text = Convert.ToString(reader[columnName]);
            if (DateTime.TryParse(text, out value))
                return value.Date;

            return DateTime.TryParseExact(
                fallbackSealNo,
                "yyMMdd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out value)
                    ? (DateTime?)value.Date
                    : null;
        }
    }
}
