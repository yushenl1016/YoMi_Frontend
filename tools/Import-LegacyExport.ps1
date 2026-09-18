param(
    [string]$SourceDirectory = 'C:\Users\siens\Downloads',
    [string]$BackupRoot = 'C:\Users\siens\Downloads\YoMi-import-backups',
    [switch]$Commit
)

# One specific export batch. Default is a verified dry run followed by rollback.
# CSV text is data only; SQL values are transferred through typed SqlBulkCopy.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$culture = [Globalization.CultureInfo]::InvariantCulture
$specs = @(
    @{ Table='FrontendMember'; File='users_20260918_120215.csv'; Key='Id'; ReadOnlyExisting=$true;
       Map=[ordered]@{Id='id';Name='name';Email='email';PasswordHash='password';Role='role';CreatedAt='createdAt';UpdatedAt='updatedAt';LastSignedIn='lastSignedIn'} },
    @{ Table='FrontendConsumptionRecord'; File='consumption_records_20260918_120228.csv'; Key='Id'; ReadOnlyExisting=$true;
       Map=[ordered]@{Id='id';UserId='userId';Amount='amount';Description='description';Category='category';CreatedBy='createdBy';CreatedAt='createdAt';UpdatedAt='updatedAt'} },
    @{ Table='FrontendPriceItem'; File='price_items_20260918_120230.csv'; Key='Id'; ReadOnlyExisting=$false;
       Map=[ordered]@{Id='id';Tab='tab';Name='name';Price='price';Note='note';SortOrder='sortOrder';CreatedAt='createdAt';UpdatedAt='updatedAt'} },
    @{ Table='FrontendSiteSetting'; File='site_settings_20260918_120232.csv'; Key='Key'; ReadOnlyExisting=$false;
       Map=[ordered]@{Key='key';Value='value';Label='label';Description='description';UpdatedAt='updatedAt'} },
    @{ Table='FrontendBackgroundAsset'; File='background_assets_20260918_120226.csv'; Key='Id'; ReadOnlyExisting=$true;
       Map=[ordered]@{Id='id';Name='name';FileKey='fileKey';FileUrl='fileUrl';MimeType='mimeType';IsActive='isActive';CreatedAt='createdAt'} }
)

$configPath = Join-Path $PSScriptRoot '..\YoMi_Frontend\appsettings.json'
$config = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
$connection = [System.Data.SqlClient.SqlConnection]::new($config.ConnectionStrings.DefaultConnection)
$transaction = $null
$committed = $false
$backupDirectory = Join-Path $BackupRoot ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
$source = @{}
foreach ($spec in $specs) {
    $source[$spec.Table] = @(Import-Csv -LiteralPath (Join-Path $SourceDirectory $spec.File) -Encoding UTF8)
    $duplicates = @($source[$spec.Table] | Group-Object -Property $spec.Map[$spec.Key] | Where-Object Count -gt 1)
    if ($duplicates.Count) { throw "Duplicate source keys in $($spec.Table)." }
}
if (@($source.FrontendMember | Group-Object { $_.email.Trim().ToLowerInvariant() } | Where-Object Count -gt 1).Count) {
    throw 'Duplicate normalized member emails.'
}
$memberIds = @{}
foreach ($member in $source.FrontendMember) {
    $memberIds[[int]$member.id] = $true
    if ($member.role -notin @('user','admin')) { throw 'Unsupported member role.' }
    # This export contains no passwords. Do not invent passwords or silently import incompatible hashes.
    if (![string]::IsNullOrEmpty($member.password)) { throw 'Nonempty legacy password needs a separate compatibility review.' }
}
[decimal]$sourceTotal = 0
foreach ($record in $source.FrontendConsumptionRecord) {
    if (!$memberIds.ContainsKey([int]$record.userId) -or
        ($record.createdBy -ne '' -and !$memberIds.ContainsKey([int]$record.createdBy))) {
        throw 'Consumption references a member absent from the export.'
    }
    $amount = [decimal]::Parse($record.amount, $culture)
    if ($amount -ne [decimal]::Round($amount,2) -or [Math]::Abs($amount) -gt 99999999.99) {
        throw 'Consumption amount is outside decimal(10,2).'
    }
    $sourceTotal += $amount
}

try {
    $connection.Open()
    $transaction = $connection.BeginTransaction([System.Data.IsolationLevel]::Serializable)
    function Command([string]$sql) {
        $cmd = $connection.CreateCommand()
        $cmd.Transaction = $transaction
        $cmd.CommandTimeout = 60
        $cmd.CommandText = $sql
        return $cmd
    }
    $setup = Command 'SET XACT_ABORT ON; SET LOCK_TIMEOUT 15000;'
    [void]$setup.ExecuteNonQuery()

    # Lock the five small destination tables while exporting and reconciling this batch.
    $before = [System.Data.DataSet]::new('BeforeLegacyImport')
    foreach ($spec in $specs) {
        $cmd = Command "SELECT * FROM dbo.[$($spec.Table)] WITH (TABLOCKX,HOLDLOCK);"
        $adapter = [System.Data.SqlClient.SqlDataAdapter]::new($cmd)
        [void]$adapter.Fill($before, $spec.Table)
        $adapter.Dispose()
    }
    [void][IO.Directory]::CreateDirectory($backupDirectory)
    $backupPath = Join-Path $backupDirectory 'before.xml'
    $before.WriteXml($backupPath, [System.Data.XmlWriteMode]::WriteSchema)
    $backupCheck = [System.Data.DataSet]::new()
    [void]$backupCheck.ReadXml($backupPath)
    foreach ($spec in $specs) {
        if ($backupCheck.Tables[$spec.Table].Rows.Count -ne $before.Tables[$spec.Table].Rows.Count) {
            throw "Backup verification failed for $($spec.Table)."
        }
        Copy-Item -LiteralPath (Join-Path $SourceDirectory $spec.File) -Destination (Join-Path $backupDirectory $spec.File)
    }

    foreach ($spec in $specs) {
        $name = $spec.Table
        $columns = @($spec.Map.Keys)
        $columnSql = ($columns | ForEach-Object { "[$_]" }) -join ','
        $data = [System.Data.DataTable]::new($name)
        foreach ($column in $columns) {
            $original = $before.Tables[$name].Columns[$column]
            $typedColumn = $data.Columns.Add($column, $original.DataType)
            $typedColumn.AllowDBNull = $original.AllowDBNull
            if ($original.DataType -eq [string]) { $typedColumn.MaxLength = $original.MaxLength }
        }
        foreach ($item in $source[$name]) {
            $row = $data.NewRow()
            foreach ($column in $columns) {
                $raw = [string]$item.($spec.Map[$column])
                $type = $data.Columns[$column].DataType
                if ($column -eq 'PasswordHash') { $value = '' }
                elseif ($column -eq 'Email') { $value = $raw.Trim().ToLowerInvariant() }
                elseif ($type -eq [datetime]) { $value = [datetime]::ParseExact($raw,'yyyy-MM-dd HH:mm:ss',$culture) }
                elseif ($type -eq [decimal]) { $value = [decimal]::Parse($raw,$culture) }
                elseif ($type -eq [int]) { $value = if ($raw -eq '' -and $data.Columns[$column].AllowDBNull) { [DBNull]::Value } else { [int]::Parse($raw,$culture) } }
                elseif ($type -eq [bool]) {
                    if ($raw -notin @('0','1')) { throw 'Invalid background active flag.' }
                    $value = $raw -eq '1'
                }
                else { $value = $raw }
                $row[$column] = $value
            }
            [void]$data.Rows.Add($row)
        }
        $stage = "#Import$name"
        $cmd = Command "SELECT TOP (0) $columnSql INTO [$stage] FROM dbo.[$name];"
        [void]$cmd.ExecuteNonQuery()
        $bulk = [System.Data.SqlClient.SqlBulkCopy]::new($connection,[System.Data.SqlClient.SqlBulkCopyOptions]::KeepIdentity,$transaction)
        try {
            $bulk.DestinationTableName = "[$stage]"
            foreach ($column in $columns) { [void]$bulk.ColumnMappings.Add($column,$column) }
            $bulk.WriteToServer($data)
        } finally { $bulk.Dispose() }

        $key = $spec.Key
        $sourceColumns = ($columns | ForEach-Object { "s.[$_]" }) -join ','
        $targetColumns = ($columns | ForEach-Object { "t.[$_]" }) -join ','
        if ($spec.ReadOnlyExisting) {
            $cmd = Command "IF EXISTS (SELECT $sourceColumns FROM [$stage] s JOIN dbo.[$name] t ON t.[$key]=s.[$key] EXCEPT SELECT $targetColumns FROM [$stage] s JOIN dbo.[$name] t ON t.[$key]=s.[$key]) THROW 50001, 'Existing member/record conflict; import rolled back.', 1;"
            [void]$cmd.ExecuteNonQuery()
        } else {
            $assignments = ($columns | Where-Object { $_ -ne $key } | ForEach-Object { "t.[$_]=s.[$_]" }) -join ','
            $cmd = Command "UPDATE t SET $assignments FROM dbo.[$name] t JOIN [$stage] s ON t.[$key]=s.[$key];"
            [void]$cmd.ExecuteNonQuery()
        }
        $identityOn = if ($columns -contains 'Id') { "SET IDENTITY_INSERT dbo.[$name] ON;" } else { '' }
        $identityOff = if ($columns -contains 'Id') { "SET IDENTITY_INSERT dbo.[$name] OFF;" } else { '' }
        $cmd = Command "$identityOn INSERT INTO dbo.[$name] ($columnSql) SELECT $sourceColumns FROM [$stage] s WHERE NOT EXISTS (SELECT 1 FROM dbo.[$name] t WHERE t.[$key]=s.[$key]); $identityOff"
        [void]$cmd.ExecuteNonQuery()
        $cmd = Command "IF EXISTS (SELECT $columnSql FROM [$stage] EXCEPT SELECT $columnSql FROM dbo.[$name]) THROW 50002, 'Imported row comparison failed.', 1; SELECT COUNT(*) FROM dbo.[$name];"
        $count = $cmd.ExecuteScalar()
        Write-Output "$name source=$($data.Rows.Count) before=$($before.Tables[$name].Rows.Count) after=$count; all mapped columns reconciled."
    }
    $check = Command @'
IF EXISTS (SELECT 1 FROM dbo.FrontendConsumptionRecord r
  JOIN #ImportFrontendConsumptionRecord s ON s.Id=r.Id
  LEFT JOIN dbo.FrontendMember u ON u.Id=r.UserId
  LEFT JOIN dbo.FrontendMember creator ON creator.Id=r.CreatedBy
  WHERE u.Id IS NULL OR (r.CreatedBy IS NOT NULL AND creator.Id IS NULL))
  THROW 50003, 'Orphaned consumption reference.', 1;
IF EXISTS (
  SELECT UserId,SUM(Amount) Total FROM #ImportFrontendConsumptionRecord GROUP BY UserId
  EXCEPT
  SELECT r.UserId,SUM(r.Amount) FROM dbo.FrontendConsumptionRecord r
  JOIN #ImportFrontendConsumptionRecord s ON s.Id=r.Id GROUP BY r.UserId)
  THROW 50004, 'Per-member consumption totals differ.', 1;
SELECT COALESCE(SUM(r.Amount),0) FROM dbo.FrontendConsumptionRecord r
JOIN #ImportFrontendConsumptionRecord s ON s.Id=r.Id;
'@
    $databaseTotal = [decimal]$check.ExecuteScalar()
    if ($databaseTotal -ne $sourceTotal) { throw 'Consumption totals differ.' }
    Write-Output "Consumption sum=$($sourceTotal.ToString('0.00',$culture)); member and creator references verified."
    if ($Commit) {
        $transaction.Commit()
        $committed = $true
        Write-Output 'COMMITTED'
    } else {
        $transaction.Rollback()
        Write-Output 'DRY RUN PASSED; all database changes rolled back. Use -Commit to import.'
    }
    Write-Output "Backup: $backupDirectory"
    Write-Output 'Legacy openId/loginMethod have no destination columns and remain preserved in the source CSV backup. Timestamps retained without timezone conversion.'
    Write-Output 'All source passwords are empty; imported accounts have no usable local password.'
} catch {
    if ($transaction -and !$committed) {
        try { $transaction.Rollback() } catch { }
    }
    throw
} finally {
    $connection.Dispose()
}
