param(
 [switch]$Commit,
 [switch]$TrustServerCertificate,
 [Parameter(Mandatory)][string]$BackupDirectory,
 [string]$ConfigurationPath=(Join-Path $PSScriptRoot '../YoMi_Frontend/appsettings.json')
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$backup=(Resolve-Path -LiteralPath $BackupDirectory).Path
$cfg=Get-Content -LiteralPath $ConfigurationPath -Raw | ConvertFrom-Json
$b=[System.Data.SqlClient.SqlConnectionStringBuilder]::new($cfg.ConnectionStrings.DefaultConnection)
if($b.InitialCatalog -ne 'YoMi_Admin'){throw 'Unexpected database; migration stopped.'}
$b['Encrypt']=$true; $b['TrustServerCertificate']=[bool]$TrustServerCertificate; $b['Connect Timeout']=10
$c=[System.Data.SqlClient.SqlConnection]::new($b.ConnectionString)
$tx=$null; $committed=$false
function Command([string]$Sql){$q=$c.CreateCommand();$q.Transaction=$tx;$q.CommandTimeout=60;$q.CommandText=$Sql;return $q}
try{
 $c.Open();$tx=$c.BeginTransaction([Data.IsolationLevel]::Serializable)
 [void](Command @'
SET XACT_ABORT ON; SET LOCK_TIMEOUT 15000;
DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=N'YoMiDesktopPet.SchemaImport',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=15000;
IF @r<0 THROW 51205,'Another migration is running.',1;
'@).ExecuteNonQuery()
 $old=[int](Command "SELECT COUNT(*) FROM sys.tables WHERE object_id=OBJECT_ID('dbo.YoMiUsers');").ExecuteScalar()
 $memberChecksumSql='SELECT CONCAT(COUNT(*),'':'',CHECKSUM_AGG(BINARY_CHECKSUM(Id,Name,Email,PasswordHash,Role,CreatedAt,UpdatedAt,LastSignedIn,HasLoggedInNewSite,Birthday))) FROM dbo.FrontendMember;'
 $before=(Command $memberChecksumSql).ExecuteScalar()
 if($old){
  $snapshot=[ordered]@{}
  foreach($table in @('YoMiUsers','YoMiDocuments','YoMiSessions')){
   $adapter=[Data.SqlClient.SqlDataAdapter]::new((Command "SELECT * FROM dbo.$table;"))
   $dt=[Data.DataTable]::new();[void]$adapter.Fill($dt)
   $rows=@(foreach($row in $dt.Rows){$item=[ordered]@{};foreach($column in $dt.Columns){$v=$row[$column.ColumnName];$item[$column.ColumnName]=if($v -is [DBNull]){$null}else{$v}};[pscustomobject]$item})
   $snapshot[$table]=$rows
  }
  $snapshotPath=Join-Path $backup ('sql-pet-before-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfff')+'.json')
  $snapshotJson=$snapshot | ConvertTo-Json -Depth 8
  [IO.File]::WriteAllText($snapshotPath,$snapshotJson,[Text.UTF8Encoding]::new($false))
  if([IO.File]::ReadAllText($snapshotPath) -cne $snapshotJson){throw 'SQL backup exact readback failed.'}
  $check=Get-Content -LiteralPath $snapshotPath -Raw | ConvertFrom-Json
  if(@($check.YoMiDocuments).Count -ne @($snapshot.YoMiDocuments).Count){throw 'SQL backup validation failed.'}
 }
 [void](Command (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'DesktopPetMemberMigration.sql') -Raw)).ExecuteNonQuery()
 [void](Command (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'DesktopPetSchema.sql') -Raw)).ExecuteNonQuery()
 [void](Command (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Test-DesktopPetMemberSchema.sql') -Raw)).ExecuteNonQuery()
 if((Command $memberChecksumSql).ExecuteScalar() -cne $before){throw 'Existing members changed; rolling back.'}
 $documents=[int](Command 'SELECT COUNT(*) FROM dbo.YoMiDocuments;').ExecuteScalar()
 $sessions=[int](Command 'SELECT COUNT(*) FROM dbo.YoMiSessions;').ExecuteScalar()
 if($Commit){$tx.Commit();$committed=$true}else{$tx.Rollback()};$tx=$null
 $report=[ordered]@{AtUtc=[DateTime]::UtcNow.ToString('o');Database='YoMi_Admin';Committed=$committed;MigratedOldSchema=[bool]$old;Documents=$documents;Sessions=$sessions;MembersUnchanged=$true;PayloadsAndRevisionsPreserved=$true;SchemaChecksPassed=$true;CertificateValidationBypassed=[bool]$TrustServerCertificate}
 $name=if($Commit){'member-migration-commit.json'}else{'member-migration-dry-run.json'}
 $report | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $backup $name)
 $report | ConvertTo-Json
}catch{
 if($committed){throw 'SQL committed, but saving the report failed; verify before retrying.'}
 throw 'Member migration failed and was not committed. Inspect the schema/mapping and backup; existing data is retained.'
}finally{if($tx){$tx.Rollback()};$c.Dispose()}
