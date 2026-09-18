param(
    [uri]$BaseUrl = 'https://localhost:44313',
    [switch]$ExpectDisabled
)

# Run against a running local app. Does not follow redirects, sign in to Google, or write the DB.
$ErrorActionPreference = 'Stop'
if (-not $BaseUrl.IsLoopback -or $BaseUrl.Scheme -ne 'https') {
    throw 'Use a local HTTPS URL with a trusted development certificate.'
}
Add-Type -AssemblyName System.Net.Http
Add-Type -AssemblyName System.Web
$handler = [System.Net.Http.HttpClientHandler]::new()
$handler.AllowAutoRedirect = $false
$client = [System.Net.Http.HttpClient]::new($handler)
$client.BaseAddress = $BaseUrl

function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

try {
    $page = $client.GetAsync('/account/google-test').GetAwaiter().GetResult()
    if ($ExpectDisabled) {
        Assert-Check ([int]$page.StatusCode -eq 404) 'Test entry must be disabled outside Development.'
        $result = $client.GetAsync('/account/google-test/result').GetAwaiter().GetResult()
        Assert-Check ([int]$result.StatusCode -eq 404) 'Test result must be disabled outside Development.'
        Write-Output 'PASS: Google test routes are disabled outside Development.'
        return
    }

    Assert-Check ([int]$page.StatusCode -eq 200) 'Test entry did not load.'
    Assert-Check ($page.Headers.CacheControl.NoStore) 'Test page must not be cached.'
    $html = $page.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    $token = [regex]::Match($html, 'name="__RequestVerificationToken"[^>]*value="([^"]+)"').Groups[1].Value
    Assert-Check (-not [string]::IsNullOrEmpty($token)) 'Missing antiforgery token.'
    $body = [System.Net.Http.StringContent]::new('__RequestVerificationToken=' + [uri]::EscapeDataString([System.Net.WebUtility]::HtmlDecode($token)), [System.Text.Encoding]::UTF8, 'application/x-www-form-urlencoded')

    $start = $client.PostAsync('/account/google-test/start', $body).GetAwaiter().GetResult()
    Assert-Check ([int]$start.StatusCode -eq 302) 'Expected a Google authorization redirect.'
    $location = $start.Headers.Location
    Assert-Check ($location.Scheme -eq 'https' -and $location.Host -eq 'accounts.google.com') 'Unexpected authorization destination.'
    $query = [System.Web.HttpUtility]::ParseQueryString($location.Query)
    Assert-Check ($query['redirect_uri'] -eq ([uri]::new($BaseUrl, '/signin-google-test')).AbsoluteUri) 'Callback URL does not match the app origin.'
    Assert-Check ($query['response_type'] -eq 'code') 'Expected authorization code flow.'
    Assert-Check ($query['code_challenge_method'] -eq 'S256' -and $query['code_challenge']) 'PKCE is missing.'
    Assert-Check ($query['state'] -and $query['nonce']) 'State or nonce is missing.'
    Assert-Check (($query['scope'].Split(' ') | Sort-Object) -join ' ' -eq 'email openid') 'Unexpected Google scopes.'
    Assert-Check (-not $query['client_secret']) 'Client secret must not be sent to the browser.'

    $missingToken = $client.PostAsync('/account/google-test/start', [System.Net.Http.StringContent]::new('')).GetAwaiter().GetResult()
    # Existing status-page re-execution changes POST errors from 400 to 405.
    Assert-Check ([int]$missingToken.StatusCode -in @(400, 405)) 'Login start accepted a request without antiforgery protection.'

    $invalidCallback = $client.GetAsync('/signin-google-test?code=invalid&state=invalid').GetAwaiter().GetResult()
    Assert-Check ([int]$invalidCallback.StatusCode -eq 302 -and $invalidCallback.Headers.Location.OriginalString -eq '/email-auth?googleError=1') 'Invalid OAuth state must be rejected.'
    $result = $client.GetAsync('/account/google-test/result').GetAwaiter().GetResult()
    Assert-Check ([int]$result.StatusCode -eq 302 -and $result.Headers.Location.OriginalString -eq '/account/google-test') 'Unauthenticated result must not disclose identity.'
    Assert-Check (-not $handler.CookieContainer.GetCookies($BaseUrl)['YoMi_Frontend.Auth']) 'Test flow must not create a member cookie.'

    $loginPage = $client.GetAsync('/email-auth').GetAwaiter().GetResult()
    $loginHtml = $loginPage.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    Assert-Check ([int]$loginPage.StatusCode -eq 200 -and $loginHtml.Contains('action="/account/google"')) 'Member login is missing the Google form.'
    Assert-Check (-not $loginHtml.Contains('action="/account/login"') -and -not $loginHtml.Contains('action="/account/register"')) 'Legacy password forms remain active.'
    foreach ($legacyPath in @('/account/login','/account/register','/account/check-email')) {
        $legacyResponse = $client.PostAsync($legacyPath, [System.Net.Http.StringContent]::new('')).GetAwaiter().GetResult()
        Assert-Check ([int]$legacyResponse.StatusCode -in @(404,405)) "Legacy endpoint remains active: $legacyPath"
    }
    $memberToken = [regex]::Match($loginHtml, 'name="__RequestVerificationToken"[^>]*value="([^"]+)"').Groups[1].Value
    $memberBody = [System.Net.Http.StringContent]::new('__RequestVerificationToken=' + [uri]::EscapeDataString([System.Net.WebUtility]::HtmlDecode($memberToken)), [System.Text.Encoding]::UTF8, 'application/x-www-form-urlencoded')
    $memberStart = $client.PostAsync('/account/google', $memberBody).GetAwaiter().GetResult()
    Assert-Check ([int]$memberStart.StatusCode -eq 302 -and $memberStart.Headers.Location.Host -eq 'accounts.google.com') 'Member Google login did not redirect to Google.'
    $complete = $client.GetAsync('/account/google-complete').GetAwaiter().GetResult()
    Assert-Check ([int]$complete.StatusCode -eq 302 -and -not $handler.CookieContainer.GetCookies($BaseUrl)['YoMi_Frontend.Auth']) 'Unauthenticated completion created a member session.'

    $publicSecret = $client.GetAsync('/google_client_secret/client_secret_271916382546-qdh9njn6elki4gkvb2p6g2kg7im683h5.apps.googleusercontent.com.json').GetAwaiter().GetResult()
    Assert-Check ([int]$publicSecret.StatusCode -eq 404) 'Original public credential URL is accessible.'
    Write-Output 'PASS: Google test/member entry, disabled legacy routes, no-store, code flow, callback URL, PKCE, nonce/state, minimal scopes, CSRF, invalid callback, member isolation, and public secret removal.'
    Write-Output 'A real Google sign-in is still required to verify the returned sub/email.'
}
finally {
    $client.Dispose()
    $handler.Dispose()
}
