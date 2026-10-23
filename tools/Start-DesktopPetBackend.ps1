$ErrorActionPreference='Stop'
$project=Join-Path $PSScriptRoot '../YoMi_Frontend/YoMi_Frontend.csproj'
$private=Join-Path $PSScriptRoot '../local-secrets'
if(!(Test-Path -LiteralPath (Join-Path $private 'desktop-api.json'))){throw 'Configure local-secrets/desktop-api.json first; see DesktopPetDatabase.md.'}
if(Get-NetTCPConnection -LocalPort 5101 -State Listen -ErrorAction SilentlyContinue){throw 'Port 5101 is already in use. Check the existing backend before starting another instance.'}
$dotnet=(Get-Command dotnet -ErrorAction Stop).Source
$oldEnvironment=$env:ASPNETCORE_ENVIRONMENT
try {
 $env:ASPNETCORE_ENVIRONMENT='Development'
 $process=Start-Process -FilePath $dotnet -ArgumentList @('run','--no-build','--no-launch-profile','--project',('"'+$project+'"'),'--urls','https://localhost:5101') -WorkingDirectory (Split-Path $project) -WindowStyle Hidden -RedirectStandardOutput (Join-Path $private 'desktop-api.stdout.log') -RedirectStandardError (Join-Path $private 'desktop-api.stderr.log') -PassThru
 $ready=$false
 for($i=0;$i -lt 40;$i++){
  Start-Sleep -Milliseconds 500
  $process.Refresh(); if($process.HasExited){throw 'Backend exited; inspect the private desktop-api logs.'}
  try { $response=Invoke-WebRequest 'https://localhost:5101/api/desktop/documents' -SkipHttpErrorCheck -TimeoutSec 2; if($response.StatusCode -eq 401){$ready=$true;break} } catch {}
 }
 if(!$ready){throw 'Backend did not pass its HTTPS authentication check; inspect the private desktop-api logs.'}
 [pscustomobject]@{ProcessId=$process.Id;Url='https://localhost:5101';AnonymousStatus=401}
}finally{$env:ASPNETCORE_ENVIRONMENT=$oldEnvironment}
