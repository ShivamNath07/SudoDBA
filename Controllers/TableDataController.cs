using System;
using System.Data;
using System.Data.SqlClient;
using System.Configuration;
using System.Web.Mvc;

namespace SudoDBA.Controllers
{
    public class TableDataController : Controller
    {
        private readonly string connString = ConfigurationManager.ConnectionStrings["CIS_UAT_DB"].ConnectionString;

        // Helper to validate allowed table names to prevent SQL injection on table identifiers
        private bool IsValidTable(string tableName)
        {
            return tableName == "PackagesForms" || tableName == "DCW" || tableName == "InlineDetails" || tableName == "CoverSheetinfo";
        }

        private string GetPrimaryKeyColumn(string tableName)
        {
            if (tableName == "CoverSheetinfo") return "FlexName";
            return "ID"; // PackagesForms, DCW, InlineDetails use 'ID'
        }

        // GET: TableData/Index?tableName=PackagesForms&searchId=val
        public ActionResult Index(string tableName, string searchId)
        {
            if (string.IsNullOrEmpty(tableName) || !IsValidTable(tableName))
            {
                tableName = "PackagesForms"; // Default fallback
            }

            ViewBag.CurrentTable = tableName;
            ViewBag.PrimaryKeyCol = GetPrimaryKeyColumn(tableName);

            DataTable dt = new DataTable();
            using (SqlConnection conn = new SqlConnection(connString))
            {
                // Fetch top 200 rows for performance
                string query = $"SELECT TOP 200 * FROM [{tableName}]";
                string pkCol = ViewBag.PrimaryKeyCol;

                if (!string.IsNullOrEmpty(searchId))
                {
                    query += $" WHERE [{pkCol}] = @SearchId";
                }

                using (SqlCommand cmd = new SqlCommand(query, conn))
                {
                    if (!string.IsNullOrEmpty(searchId))
                    {
                        cmd.Parameters.AddWithValue("@SearchId", searchId);
                    }
                    using (SqlDataAdapter da = new SqlDataAdapter(cmd))
                    {
                        da.Fill(dt);
                    }
                }
            }
            return View(dt);
        }

        // POST: TableData/UpdateRow
        [HttpPost]
        public ActionResult UpdateRow(string tableName, string recordKey, string pcrNumber, string columnName, string newValue)
        {
            if (string.IsNullOrEmpty(pcrNumber))
            {
                TempData["Error"] = "PCR Number is mandatory before making any changes!";
                return RedirectToAction("Index", new { tableName = tableName });
            }

            if (!IsValidTable(tableName))
            {
                TempData["Error"] = "Invalid table target.";
                return RedirectToAction("Index", new { tableName = "PackagesForms" });
            }

            string pkCol = GetPrimaryKeyColumn(tableName);
            string currentUser = User.Identity.Name ?? "InternalUser";

            using (SqlConnection conn = new SqlConnection(connString))
            {
                conn.Open();
                SqlTransaction transaction = conn.BeginTransaction();

                try
                {
                    // 1. Capture Old Value & Log Audit History with PCR
                    string auditQuery = $@"INSERT INTO DataAuditHistory (TableName, RecordKey, PCRNumber, ModifiedBy, ModifiedDate, ColumnName, OldValue, NewValue)
                                          SELECT @Tbl, @RecKey, @PCR, @User, GETDATE(), @ColName, 
                                          (SELECT TOP 1 [{columnName}] FROM [{tableName}] WHERE [{pkCol}] = @RecKey), @NewVal";

                    using (SqlCommand auditCmd = new SqlCommand(auditQuery, conn, transaction))
                    {
                        auditCmd.Parameters.AddWithValue("@Tbl", tableName);
                        auditCmd.Parameters.AddWithValue("@RecKey", recordKey);
                        auditCmd.Parameters.AddWithValue("@PCR", pcrNumber);
                        auditCmd.Parameters.AddWithValue("@User", currentUser);
                        auditCmd.Parameters.AddWithValue("@ColName", columnName);
                        auditCmd.Parameters.AddWithValue("@NewVal", (object)newValue ?? DBNull.Value);
                        auditCmd.ExecuteNonQuery();
                    }

                    // 2. Execute Secure Parameterized Update
                    string updateQuery = $"UPDATE [{tableName}] SET [{columnName}] = @NewVal WHERE [{pkCol}] = @RecKey";
                    using (SqlCommand updateCmd = new SqlCommand(updateQuery, conn, transaction))
                    {
                        updateCmd.Parameters.AddWithValue("@NewVal", (object)newValue ?? DBNull.Value);
                        updateCmd.Parameters.AddWithValue("@RecKey", recordKey);
                        updateCmd.ExecuteNonQuery();
                    }

                    transaction.Commit();
                    TempData["Success"] = $"Successfully updated [{tableName}] (Key: {recordKey}) under PCR: {pcrNumber}";
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    TempData["Error"] = "Update failed: " + ex.Message;
                }
            }

            return RedirectToAction("Index", new { tableName = tableName });
        }
    }
}