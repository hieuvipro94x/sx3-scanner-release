using SX3_SCANER.Helper;
using SX3_SCANER.Model.Respository;
using System;
using System.Data.SQLite;
using System.Globalization;

namespace SX3_SCANER.Model
{
    internal class DatabaseInitialize
    {
        private const int MainDatabaseSchemaVersion = 5;
        private const int ProductDatabaseSchemaVersion = 1;
        private const string LastIntegrityCheckKey = "LastIntegrityCheckUtc";
        private static readonly TimeSpan IntegrityCheckInterval = TimeSpan.FromDays(7);

        internal void EnsureCreate()
        {
            StartupManager.SetStatus("\u0110ang ki\u1EC3m tra th\u01B0 m\u1EE5c d\u1EEF li\u1EC7u...");
            DatabaseRepository.EnsureDatabaseFiles();
            DatabaseRepository.ValidateDatabasePaths();

            StartupManager.SetStatus("\u0110ang ki\u1EC3m tra database.db...");
            ConfigureDatabase();
            StartupManager.SetStatus("\u0110ang ki\u1EC3m tra product.db...");
            ConfigureProductDatabase();

            ApplyMigrationsIfNeeded();
            RunPeriodicIntegrityCheck();
            StartupManager.SetStatus("Ho\u00E0n t\u1EA5t kh\u1EDFi \u0111\u1ED9ng");
        }

        private static void ApplyMigrationsIfNeeded()
        {
            int mainVersion = GetUserVersion(DatabaseRepository.CreateConnection);
            if (mainVersion < MainDatabaseSchemaVersion)
            {
                DatabaseRepository.BackupDatabaseFile(
                    DatabaseRepository.DatabasePath,
                    "main schema " + mainVersion + " -> " +
                    MainDatabaseSchemaVersion);
                StartupManager.SetStatus("\u0110ang c\u1EADp nh\u1EADt c\u1EA5u tr\u00FAc database.db...");
                BoxProductRepository.CreateTableIfNotExists();
                ScanHistoryRepository.CreateTableIfNotExists();
                ScanSessionService.CreateTableIfNotExists();
                SyncHistoryBoxTypes();
                CreateMainIndexes();
                CreateDataIntegrityTriggers();
                SetUserVersion(
                    DatabaseRepository.CreateConnection,
                    MainDatabaseSchemaVersion);
            }
            else
            {
                // Keep BoxProduct compatible even if an older deployment has an
                // incorrect user_version or a partially migrated table.
                BoxProductRepository.CreateTableIfNotExists();
            }

            int productVersion = GetUserVersion(DatabaseRepository.CreateProductConnection);
            if (productVersion < ProductDatabaseSchemaVersion)
            {
                DatabaseRepository.BackupDatabaseFile(
                    DatabaseRepository.ProductDatabasePath,
                    "product schema " + productVersion + " -> " +
                    ProductDatabaseSchemaVersion);
                StartupManager.SetStatus("\u0110ang c\u1EADp nh\u1EADt c\u1EA5u tr\u00FAc product.db...");
                LabelProductInfoRepository.CreateTableIfNotExists();
                CreateProductIndexes();
                SetUserVersion(
                    DatabaseRepository.CreateProductConnection,
                    ProductDatabaseSchemaVersion);
            }
        }

        private static int GetUserVersion(Func<SQLiteConnection> connectionFactory)
        {
            using (SQLiteConnection connection = connectionFactory())
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA user_version;";
                return Convert.ToInt32(command.ExecuteScalar());
            }
        }

        private static void SetUserVersion(
            Func<SQLiteConnection> connectionFactory,
            int version)
        {
            using (SQLiteConnection connection = connectionFactory())
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA user_version = " + version + ";";
                command.ExecuteNonQuery();
            }
        }

        private static void RunPeriodicIntegrityCheck()
        {
            DateTime lastCheckUtc;
            string storedValue = AppConfigHelper.Read(LastIntegrityCheckKey);
            bool recentlyChecked =
                DateTime.TryParse(
                    storedValue,
                    null,
                    DateTimeStyles.RoundtripKind,
                    out lastCheckUtc) &&
                DateTime.UtcNow - lastCheckUtc.ToUniversalTime() < IntegrityCheckInterval;

            if (recentlyChecked)
                return;

            StartupManager.SetStatus("\u0110ang ki\u1EC3m tra t\u00EDnh to\u00E0n v\u1EB9n d\u1EEF li\u1EC7u...");
            DatabaseRepository.RunIntegrityCheck();
            AppConfigHelper.Modify(
                LastIntegrityCheckKey,
                DateTime.UtcNow.ToString("O"));
        }

        private static void SyncHistoryBoxTypes()
        {
            TryExecute(@"
                UPDATE ScanHistoryView
                SET BoxType = COALESCE(
                        (SELECT BoxType FROM BoxProduct WHERE BoxProduct.BoxName = ScanHistoryView.BoxName LIMIT 1),
                        BoxType),
                    IsPartialBox = COALESCE(
                        (SELECT IsPartialBox FROM BoxProduct WHERE BoxProduct.BoxName = ScanHistoryView.BoxName LIMIT 1),
                        IsPartialBox)
                WHERE EXISTS (
                    SELECT 1 FROM BoxProduct WHERE BoxProduct.BoxName = ScanHistoryView.BoxName
                );");
        }

        private static void ConfigureDatabase()
        {
            using (SQLiteConnection connection = DatabaseRepository.CreateConnection())
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = @"
PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;
PRAGMA foreign_keys = ON;
PRAGMA busy_timeout = 5000;";
                command.ExecuteNonQuery();
            }
        }

        private static void ConfigureProductDatabase()
        {
            using (SQLiteConnection connection = DatabaseRepository.CreateProductConnection())
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = @"
PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;
PRAGMA foreign_keys = ON;
PRAGMA busy_timeout = 5000;";
                command.ExecuteNonQuery();
            }
        }

        private static void CreateMainIndexes()
        {
            TryExecute("CREATE INDEX IF NOT EXISTS idx_ScanHistoryView_BoxName ON ScanHistoryView(BoxName);");
            TryExecute("CREATE INDEX IF NOT EXISTS idx_ScanHistoryView_BoxName_ScanTime ON ScanHistoryView(BoxName, ScanTime DESC);");
            TryExecute("CREATE INDEX IF NOT EXISTS idx_ScanHistoryView_ProductPartNumber ON ScanHistoryView(ProductPartNumber);");
            TryExecute("CREATE INDEX IF NOT EXISTS idx_ScanHistoryView_SealNo ON ScanHistoryView(SealNo);");
            TryExecute("CREATE INDEX IF NOT EXISTS idx_ScanHistoryView_LotNo ON ScanHistoryView(LotNo);");
            TryExecute("CREATE INDEX IF NOT EXISTS idx_ScanHistoryView_Result ON ScanHistoryView(ScanResult);");
            TryExecute("CREATE INDEX IF NOT EXISTS idx_ScanHistoryView_ScanTime ON ScanHistoryView(ScanTime);");
            TryExecute("CREATE INDEX IF NOT EXISTS idx_ScanHistoryView_ID_ScanTime ON ScanHistoryView(ID DESC, ScanTime DESC);");
            TryExecute("CREATE INDEX IF NOT EXISTS idx_ScanHistoryView_ScanData ON ScanHistoryView(ScanData);");
            TryExecute("CREATE UNIQUE INDEX IF NOT EXISTS UX_ScanHistoryView_PassScanData ON ScanHistoryView(ScanData COLLATE NOCASE) WHERE ScanResult = 1 AND ScanData IS NOT NULL AND TRIM(ScanData) <> '';");
            TryExecute("CREATE INDEX IF NOT EXISTS idx_ScanHistoryView_ScanMessage ON ScanHistoryView(ScanMessage);");
            TryExecute("CREATE INDEX IF NOT EXISTS idx_ScanHistoryView_ScanWorker ON ScanHistoryView(ScanWorker);");
            TryExecute("CREATE INDEX IF NOT EXISTS idx_ScanHistoryView_BoxType ON ScanHistoryView(BoxType);");
            TryExecute("CREATE INDEX IF NOT EXISTS idx_ScanHistoryView_Product_Seal_Lot_Result ON ScanHistoryView(ProductPartName, SealNo, LotNo, ScanResult);");
            TryExecute("CREATE INDEX IF NOT EXISTS idx_ScanHistoryView_PartNumber_Seal_Lot_Result ON ScanHistoryView(ProductPartNumber, SealNo, LotNo, ScanResult);");
            TryExecute("CREATE INDEX IF NOT EXISTS idx_ScanHistoryView_Pass_Product_Seal_Lot ON ScanHistoryView(ProductPartName, SealNo, LotNo) WHERE ScanResult = 1;");
            TryExecute("CREATE INDEX IF NOT EXISTS idx_BoxProduct_BoxName ON BoxProduct(BoxName);");
            TryExecute("CREATE INDEX IF NOT EXISTS idx_BoxProduct_Complete ON BoxProduct(BoxComplete);");
            TryExecute("CREATE INDEX IF NOT EXISTS idx_BoxProduct_Part_Seal ON BoxProduct(ProductPartNumber, BoxSealNo);");
        }

        private static void CreateDataIntegrityTriggers()
        {
            TryExecute(@"
                CREATE TRIGGER IF NOT EXISTS trg_ScanHistory_UniquePassScanData_Insert
                BEFORE INSERT ON ScanHistoryView
                WHEN NEW.ScanResult = 1
                  AND NEW.ScanData IS NOT NULL
                  AND TRIM(NEW.ScanData) <> ''
                  AND EXISTS (
                      SELECT 1
                      FROM ScanHistoryView
                      WHERE ScanResult = 1
                        AND ScanData = NEW.ScanData COLLATE NOCASE
                  )
                BEGIN
                    SELECT RAISE(ABORT, 'DUPLICATE_SCAN_DATA');
                END;");

            TryExecute(@"
                CREATE TRIGGER IF NOT EXISTS trg_BoxProduct_UniqueBoxName_Insert
                BEFORE INSERT ON BoxProduct
                WHEN NEW.BoxName IS NOT NULL
                  AND NEW.BoxName <> ''
                  AND EXISTS (
                      SELECT 1
                      FROM BoxProduct
                      WHERE BoxName = NEW.BoxName COLLATE NOCASE
                  )
                BEGIN
                    SELECT RAISE(ABORT, 'DUPLICATE_BOX_NAME');
                END;");

            TryExecute(@"
                CREATE TRIGGER IF NOT EXISTS trg_BoxProduct_UniqueBoxName_Update
                BEFORE UPDATE OF BoxName ON BoxProduct
                WHEN NEW.BoxName IS NOT NULL
                  AND NEW.BoxName <> ''
                  AND EXISTS (
                      SELECT 1
                      FROM BoxProduct
                      WHERE BoxName = NEW.BoxName COLLATE NOCASE
                        AND ID <> OLD.ID
                  )
                BEGIN
                    SELECT RAISE(ABORT, 'DUPLICATE_BOX_NAME');
                END;");

            TryExecute(@"
                CREATE TRIGGER IF NOT EXISTS trg_ScanHistory_UniquePassLot_Insert
                BEFORE INSERT ON ScanHistoryView
                WHEN NEW.ScanResult = 1
                  AND NEW.ProductPartNumber IS NOT NULL
                  AND NEW.SealNo IS NOT NULL
                  AND NEW.LotNo IS NOT NULL
                  AND EXISTS (
                      SELECT 1
                      FROM ScanHistoryView
                      WHERE ScanResult = 1
                        AND ProductPartNumber = NEW.ProductPartNumber COLLATE NOCASE
                        AND SealNo = NEW.SealNo COLLATE NOCASE
                        AND LotNo = NEW.LotNo COLLATE NOCASE
                  )
                BEGIN
                    SELECT RAISE(ABORT, 'DUPLICATE_PASS_LOT');
                END;");

            TryExecute(@"
                CREATE TRIGGER IF NOT EXISTS trg_ScanHistory_UniquePassLot_Update
                BEFORE UPDATE OF ProductPartNumber, SealNo, LotNo, ScanResult
                ON ScanHistoryView
                WHEN NEW.ScanResult = 1
                  AND NEW.ProductPartNumber IS NOT NULL
                  AND NEW.SealNo IS NOT NULL
                  AND NEW.LotNo IS NOT NULL
                  AND EXISTS (
                      SELECT 1
                      FROM ScanHistoryView
                      WHERE ScanResult = 1
                        AND ProductPartNumber = NEW.ProductPartNumber COLLATE NOCASE
                        AND SealNo = NEW.SealNo COLLATE NOCASE
                        AND LotNo = NEW.LotNo COLLATE NOCASE
                        AND ID <> OLD.ID
                  )
                BEGIN
                    SELECT RAISE(ABORT, 'DUPLICATE_PASS_LOT');
                END;");
        }

        private static void CreateProductIndexes()
        {
            TryExecuteProduct("CREATE INDEX IF NOT EXISTS idx_LabelProductInfo_PartNumber ON LabelProductInfo(PartNumber);");
            TryExecuteProduct("CREATE INDEX IF NOT EXISTS idx_LabelProductInfo_PartName_PartNumber ON LabelProductInfo(PartName, PartNumber);");
        }

        private static void TryExecute(string sql)
        {
            try
            {
                DatabaseRepository.ExecuteNonQuery(sql);
            }
            catch (Exception ex)
            {
                StartupManager.Log("Khong tao duoc index tuy chon. SQL=" + sql + ". Chi tiet: " + ex);
            }
        }

        private static void TryExecuteProduct(string sql)
        {
            try
            {
                using (SQLiteConnection connection = DatabaseRepository.CreateProductConnection())
                using (SQLiteCommand command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    command.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                StartupManager.Log("Khong tao duoc product index tuy chon. SQL=" + sql + ". Chi tiet: " + ex);
            }
        }
    }
}
