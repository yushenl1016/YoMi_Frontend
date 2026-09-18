# Google 登入設定與驗證

## 環境與憑證

程式依 `ASPNETCORE_ENVIRONMENT` 讀取 ASP.NET Core 標準設定；憑證路徑相對於網站 ContentRoot，也可指定絕對路徑。JSON 必須是 Google Console 匯出的 Web OAuth client，放在 `wwwroot` 外，部署時另行放置，不提交版本控制。

| 環境 | 設定檔 | Authentication:Google:CredentialsPath | CallbackPath |
| --- | --- | --- | --- |
| 本機 Development | appsettings.Development.json | ../local-secrets/google-client.json（現有檔案） | /signin-google-test |
| 測試機 Staging | appsettings.Staging.json | ../local-secrets/google-client.Staging.json（另行放置） | /signin-google |
| 正式機 Production | appsettings.json | ../local-secrets/google-client.Production.json（另行放置） | /signin-google |

Google Console 必須登記實際網站的 HTTPS 網域／連接埠加上 CallbackPath。本機目前是 `https://localhost:44313/signin-google-test`；正式機與測試機需各自使用其網域。不需要 JavaScript 來源。正式機、測試機 JSON 尚未提供，不會拿本機憑證代替；缺少檔案時登入頁顯示尚未設定，不開放 Google 登入。修改檔案或設定後重新啟動網站。若部署在反向代理後，需由部署環境正確提供外部 HTTPS origin，不能以關閉憑證驗證處理。

## 會員遷移規則

- `FrontendMember.PasswordHash` 現在存 **Google sub**，不是密碼雜湊。原密碼登入／註冊 action 與 Razor 表單已註解保留，不能直接重新啟用。
- `HasLoggedInNewSite`：舊資料預設 false，Google 會員登入成功後 true；獨立 `/account/google-test` 不改會員。
- `SysConfig.Id = FrontendGoogleMigrationCutoff`，`Value = 2026-09-18 21:44:04`，固定為台灣時間（UTC+8），格式 `yyyy-MM-dd HH:mm:ss`。程式轉成 UTC 與會員 CreatedAt 比較，每次登入讀取最新值，不快取、不在啟動時改為現在。
- 已存在 Email：CreatedAt 不晚於截止時間 **且**旗標 false，允許以 Google 驗證過的 Email 首次綁定 sub；其他情況必須 Email、sub 都相符。缺少或格式錯誤的截止設定不允許 Email-only 綁定。
- 不存在 Email：自動建立一般會員，存 sub，旗標 true。不因 BootstrapAdminEmail 自動授權為管理員。同一 sub 已屬於另一 Email 時拒絕新增或自動合併。
- 所有會員登入均需 Google 驗證簽章、issuer、audience、有效期、state／nonce／PKCE，並要求 email_verified=true；首次綁定和旗標更新在同一資料庫交易內完成。舊登入 Cookie 不含 google_sub，會要求重新 Google 登入。
- 注意：Google 對第三方信箱不一定能保證目前所有權，即使 email_verified=true。這裡依需求採 Email-only 首次遷移；若需支援高風險帳號或非 Gmail／Workspace 信箱，上線前應考慮額外驗證或人工綁定，不要任意延後截止時間或重設旗標。

## 驗證

1. 隔離規則檢查（只使用記憶體 SQLite，不連共用 SQL Server）：

   ```powershell
   dotnet build tools/GoogleLoginChecks/GoogleLoginChecks.csproj -p:OutputPath=bin/GoogleLoginChecks/
   dotnet tools/GoogleLoginChecks/bin/GoogleLoginChecks/GoogleLoginChecks.dll
   ```

2. 本機啟動後執行 `tools/Test-GoogleLogin.ps1 -BaseUrl https://localhost:44313`，檢查表單、Google 導向、回呼拒絕及舊登入端點停用；不會完成登入或寫入會員。
3. 使用原 Google 帳號在 `/email-auth` 登入一次；檢查相同會員 ID、PasswordHash=sub、HasLoggedInNewSite=1，原角色與消費紀錄不變。再登入一次應嚴格比對 sub。這一步會真的綁定會員，需使用者親自操作。

Schema migration：`20260918134404_AddFrontendGoogleLogin`，位於共用 Admin migrations。`tools/Apply-GoogleLoginMigration.ps1` 預設備份並回滾演練，`-Commit` 才套用，已有 migration 時不重設任何旗標或截止時間。此登入資料轉換不支援自動 Down，以避免重新開放帳號綁定；需復原時先確認備份與後續新增資料。
