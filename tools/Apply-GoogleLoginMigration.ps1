param(
    [switch]$Commit,
    [string]$BackupRoot = 'C:\Users\siens\Downloads\YoMi-google-login-backups'
)

# Applies only 20260918134404_AddFrontendGoogleLogin. Default: verified rollback.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$config = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\YoMi_Frontend\appsettings.json') | ConvertFrom-Json
$connection = [System.Data.SqlClient.SqlConnection]::new($config.ConnectionStrings.DefaultConnection)
$transaction = $null
try {
    $connection.Open()
    $transaction = $connection.BeginTransaction([System.Data.IsolationLevel]::Serializable)
    function Command([string]$Sql) {
        $command = $connection.CreateCommand()
        $command.Transaction = $transaction
        $command.CommandTimeout = 60
        $command.CommandText = $Sql
        return $command
    }
    [void](Command 'SET XACT_ABORT ON; SET LOCK_TIMEOUT 15000;').ExecuteNonQuery()
    $historyExists = [int](Command "SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId = N'20260918134404_AddFrontendGoogleLogin';").ExecuteScalar() -eq 1
    $flagExists = (Command "SELECT COL_LENGTH('dbo.FrontendMember', 'HasLoggedInNewSite');").ExecuteScalar() -isnot [DBNull]
    if ($historyExists -ne $flagExists) { throw 'Schema/history mismatch; nothing applied.' }
    if (-not $historyExists -and (Command 'SELECT TOP (1) MigrationId FROM dbo.__EFMigrationsHistory ORDER BY MigrationId DESC;').ExecuteScalar() -ne '20260912180627_AddFrontendPortal') {
        throw 'Unexpected migration baseline; nothing applied.'
    }

    $backup = [System.Data.DataSet]::new('BeforeGoogleLoginMigration')
    foreach ($table in @('FrontendMember','FrontendConsumptionRecord','SysConfig','__EFMigrationsHistory')) {
        $adapter = [System.Data.SqlClient.SqlDataAdapter]::new((Command "SELECT * FROM dbo.[$table] WITH (TABLOCKX,HOLDLOCK);"))
        [void]$adapter.Fill($backup, $table)
        $adapter.Dispose()
    }
    $backupDirectory = Join-Path $BackupRoot ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8))
    [void][IO.Directory]::CreateDirectory($backupDirectory)
    $backupPath = Join-Path $backupDirectory 'before.xml'
    $backup.WriteXml($backupPath, [System.Data.XmlWriteMode]::WriteSchema)
    $backupCheck = [System.Data.DataSet]::new()
    [void]$backupCheck.ReadXml($backupPath)
    foreach ($table in $backup.Tables) {
        if ($backupCheck.Tables[$table.TableName].Rows.Count -ne $table.Rows.Count) { throw 'Backup verification failed.' }
    }
    [void](Command @'
SELECT Id,Name,Email,PasswordHash,Role,CreatedAt,UpdatedAt,LastSignedIn INTO #BeforeMember FROM dbo.FrontendMember;
SELECT * INTO #BeforeRecords FROM dbo.FrontendConsumptionRecord;
SELECT * INTO #BeforeConfig FROM dbo.SysConfig;
'@).ExecuteNonQuery()

    if (-not $historyExists) {
        [void](Command 'ALTER TABLE dbo.FrontendMember ADD HasLoggedInNewSite bit NOT NULL DEFAULT (0);').ExecuteNonQuery()
        [void](Command @'
IF NOT EXISTS (SELECT 1 FROM dbo.SysConfig WHERE Id = N'FrontendGoogleMigrationCutoff')
    INSERT INTO dbo.SysConfig (Id,Value,Note,TypeName)
    VALUES (N'FrontendGoogleMigrationCutoff', N'2026-09-18 21:44:04',
        N'台灣時間 yyyy-MM-dd HH:mm:ss；建立時間不晚於此時間且尚未登入新站的會員，首次 Google 登入允許以已驗證 Email 綁定 sub。', N'Frontend');
INSERT INTO dbo.__EFMigrationsHistory (MigrationId,ProductVersion)
VALUES (N'20260918134404_AddFrontendGoogleLogin',N'5.0.10');
'@).ExecuteNonQuery()
        if ([int](Command 'SELECT COUNT(*) FROM dbo.FrontendMember WHERE HasLoggedInNewSite <> 0;').ExecuteScalar() -ne 0) {
            throw 'Existing members were unexpectedly marked as migrated.'
        }
    }
    [void](Command @'
IF EXISTS (SELECT * FROM #BeforeMember EXCEPT SELECT Id,Name,Email,PasswordHash,Role,CreatedAt,UpdatedAt,LastSignedIn FROM dbo.FrontendMember)
 OR EXISTS (SELECT Id,Name,Email,PasswordHash,Role,CreatedAt,UpdatedAt,LastSignedIn FROM dbo.FrontendMember EXCEPT SELECT * FROM #BeforeMember)
    THROW 50001, 'Member data changed unexpectedly.', 1;
IF EXISTS (SELECT * FROM #BeforeRecords EXCEPT SELECT * FROM dbo.FrontendConsumptionRecord)
 OR EXISTS (SELECT * FROM dbo.FrontendConsumptionRecord EXCEPT SELECT * FROM #BeforeRecords)
    THROW 50002, 'Consumption data changed unexpectedly.', 1;
IF EXISTS (SELECT * FROM #BeforeConfig EXCEPT SELECT * FROM dbo.SysConfig)
    THROW 50003, 'Existing system config changed unexpectedly.', 1;
'@).ExecuteNonQuery()
    Write-Output "Verified unchanged: $($backup.Tables['FrontendMember'].Rows.Count) members and $($backup.Tables['FrontendConsumptionRecord'].Rows.Count) consumption records."
    if ($Commit) { $transaction.Commit(); Write-Output 'COMMITTED' }
    else { $transaction.Rollback(); Write-Output 'DRY RUN PASSED; rolled back.' }
    $transaction = $null
    Write-Output "Backup: $backupPath"
}
finally {
    if ($transaction) { $transaction.Rollback() }
    $connection.Dispose()
}
