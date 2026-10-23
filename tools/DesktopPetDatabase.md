# YoMi_Desktop_Pet：共用前台會員與 MSSQL

桌寵已透過既有 YoMi_Frontend 的 HTTPS API 自動儲存進度。沿用 `YoMi_Frontend/appsettings.json` 的 `ConnectionStrings:DefaultConnection`，資料庫為 `172.16.0.58 / YoMi_Admin`；桌寵不保存 SQL 帳密。

## 共用資料與關聯

已讀取現有資料庫結構與會員關聯。帳號唯一來源為 `dbo.FrontendMember`；117 名既有會員保留。原有桌寵帳號的 Google subject 精確對應一名已驗證前台會員，沒有以 Email 單獨合併。初版曾新增 YoMiUsers、YoMiDocuments、YoMiSessions；後續會員整合移除 YoMiUsers，並將兩張桌寵資料表改接 FrontendMember，沒有再建立另一套帳號表。

| 現有表格 | 使用方式 |
| --- | --- |
| FrontendMember | Id 為會員鍵。沿用現有 GoogleMemberLogin；已驗證 Google subject 位於 PasswordHash，HasLoggedInNewSite 表示已登入新版 |
| YoMiDocuments | 原表改用 FrontendMemberId + DocumentKind 主鍵及會員外鍵；profile/music JSON、Revision、UpdatedAtUtc |
| YoMiSessions | 原表改用 FrontendMemberId 外鍵；TokenHash 為隨機 256-bit token 的 SHA-256、GoogleSubject 綁定、期限與撤銷時間 |

網站會員、訂單、消費及推薦資料沿用既有結構。網站 cookie 與桌寵 bearer session 用途不同，不能拿網站 cookie 或用戶端自稱的會員 ID 當作桌寵授權。會員的 Google 綁定若改變，舊桌寵 session 失效。

## 遷移與新環境

工具需要 PowerShell 7 與 SQL Server 連線權限。先結束桌寵、完整備份 Data，再執行：

```powershell
# 預設試跑，整筆交易回復。
./tools/Apply-DesktopPetMemberMigration.ps1 -BackupDirectory '<既有備份資料夾>' -TrustServerCertificate
# 試跑通過後正式提交。
./tools/Apply-DesktopPetMemberMigration.ps1 -BackupDirectory '<同一備份資料夾>' -TrustServerCertificate -Commit
```

`DesktopPetMemberMigration.sql` 在同一 SERIALIZABLE 交易中驗證 Google subject 唯一對應，轉換兩張原表的會員外鍵，逐份核對文件長度、SHA-256、版本與時間，最後才移除 YoMiUsers。工具另備份 SQL 原始列並逐字讀回核對；會員內容的檢查通過才提交。失敗回復整筆交易。重跑不會重建 YoMiUsers 或覆寫文件。

`DesktopPetSchema.sql` 是目前標準結構；新環境必須先有既有網站的 FrontendMember，再建立缺少的 YoMiDocuments/YoMiSessions。不可直接在另一個新資料庫另造帳號表。`Test-DesktopPetMemberSchema.sql` 在交易內檢查主鍵、JSON、外鍵、版本與 token 唯一性，測試列回復不保留。舊 Apply-DesktopPetMigration / Test-DesktopPetSchema / Test-DesktopPetBackup 已停止使用；歷史版本保留在備份。

本次備份位於 `../YoMi_Desktop_Pet/backups/before-member-sync-20261017-180500`，含原 Data、來源檔案、SQL 原始列、正式遷移報告及重新登入前的 pre-live-Data。原始首次匯入備份 `before-mssql-20261017-174440` 也保留。正式轉換保留兩份 JSON 的原始文字與 Revision；後續同步才更新可同步欄位。原始快照與本機裝置欄位均保留在備份及 Data。

不要把備份整份回蓋到正在使用的資料庫。若要還原，先停止桌寵與 API，另備份最新資料，再由管理員核對時間與版本後執行專門的還原交易。

## 本機後端設定與啟動

`YoMi_Frontend/Program.cs` 讀取被 Git 忽略的 `local-secrets/desktop-api.json`。只需要 Desktop OAuth 的 client ID，不複製 client secret：

```json
{
  "DesktopPet": {
    "GoogleClientIds": ["<電腦版 OAuth client_id>"],
    "TrustServerCertificate": true
  }
}
```

先使用既有 .NET SDK 建置 `YoMi_Frontend/YoMi_Frontend.csproj`，確認本機 HTTPS 開發憑證受信任，再於 PowerShell 7 執行 `./tools/Start-DesktopPetBackend.ps1`。工具在背景啟動 `https://localhost:5101`，通過匿名請求回傳 JSON 401 的檢查才回報成功；日誌留在被忽略的 local-secrets。後端必須保持執行，重新開機後需重新啟動。

桌寵 `resources/app/backend.json` 設為同一 HTTPS 網址。正式站部署完成後才改為正式 HTTPS origin；換網址後須重登，session 不會送到其他 origin。此版本不把網站服務註冊成 Windows 常駐服務。

## 實際 API

| 路徑 | 授權與行為 |
| --- | --- |
| POST /api/desktop/auth/google | body: idToken。後端驗證 Google 簽章、RS256、issuer、Desktop audience、期限及 email_verified；共用 GoogleMemberLogin，桌寵禁止舊會員 Email-only 綁定。成功取得 token、expiresAt、memberId、sub |
| GET /api/desktop/documents | Authorization: Bearer token。僅回傳該 FrontendMember 的文件、版本與更新時間 |
| PUT /api/desktop/documents/{profile\|music} | body: payload、revision。後端由 session 決定會員，禁止客戶端指定他人 ID；原子比對版本後更新，過期版本回傳 409 |
| POST /api/desktop/auth/refresh | Authorization: Bearer token。原子驗證未到期、未撤銷及會員 Google 綁定後，將原 session 到期時間延長至伺服器現在起 30 天；回傳 memberId、sub、expiresAt，不新增資料列 |
| POST /api/desktop/auth/logout | 撤銷目前 token；過期／撤銷／會員 Google 綁定改變均不能存取 |

0.7.1 起登入 session 採自動續期：每次程式啟動的首次同步先續期，持續執行時每 24 小時續期一次。成功續期延長至伺服器現在起 30 天；連續 30 天未成功續期、已登出／撤銷或 Google 綁定改變，需重新登入。刪除登入資料或重設 Windows 也需重新登入，不蒐集硬體序號。沿用同一隨機 token，避免回覆遺失導致客戶端拿不到新 token；本機到期時間僅供快取，伺服器裁定是否有效。離線不刪憑證，重新連線會再試；伺服器回覆 401 才清除該憑證，帳號進度保留。明文只保存在 Windows safeStorage 加密檔，SQL 僅存雜湊。Google ID token 只在桌寵主程序短暫傳給 API，不經 renderer、不寫磁碟。API 僅允許 HTTPS、限制 body 大小與登入頻率，錯誤不洩露 SQL 或 token。

## 同步與資料保留

桌寵先寫 Data，再每 15 秒同步；開啟程式也會同步。訪客只保存在本機。斷線保留待送進度，恢復後重試；登入到期提示重登。

profile 同步 profile、focus、inventory、equipped、baseLayout、games，focus.isRunning 固定 false。視窗位置、桌寵模式、開機與系統列等 settings 保留本機。music 同步 YouTube 收藏、歌單及播放偏好；本機音樂路徑、播放歷史、佇列與檔案留在裝置。若合併會超過原音樂庫容量，保留兩份資料並提示整理，不截斷本機內容。

`sync-state.json` 保存已確認版本與送出快照，解決回覆遺失及傳送期間又產生新進度的情形。兩端都修改時不相加幣值、不直接覆蓋；備份在帳號資料夾 `sync-backups`，由使用者選擇採用哪一份。採用伺服器內容會先備份，再重新開啟套用；執行中的記憶體存檔不能覆蓋剛下載的資料。

本次首次同步基準只取自已逐字比對原始 SQL 的首次匯入快照，沒有把較舊快照回蓋到目前 Data。

## 驗證與限制

`dotnet run --project tools/GoogleLoginChecks` 驗證網站與桌寵登入政策（隔離 SQLite）；`dotnet run --project tools/DesktopPetChecks` 驗證 JWT 與文件限制。加上 `-- --sql <appsettings完整路徑> <DesktopPetMemberMigration.sql完整路徑>` 時，在回復交易中驗證真實 SQL 的 session 所有權、撤銷、Google 綁定與版本衝突。桌寵 `npm test` 驗證離線、資料保留與同步衝突；`npm run test:desktop` 使用隔離 TestData。

真實 Google 登入已共用原會員，SQL 讀回與本機進度一致；報告位於桌寵 `verification/member-sync-*.json`。

SQL 連線加密，但內網 SQL 憑證鏈目前不受信任，因此明確設定 TrustServerCertificate。正式上線應配置可信任 SQL 憑證並移除此例外。未變更系統憑證庫或原網站連線設定。

建置仍回報既有 YoMi_Admin.Library 的 System.Data.SqlClient 4.8.5 高風險套件警告（NU1903）；本次未升級共用函式庫。此同步保存個人進度，幣值由客戶端提供，不可直接當作可兌現或對抗作弊的交易餘額。

