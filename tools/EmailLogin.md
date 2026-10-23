# Email 登入

1. `/email-auth` 先只輸入 Email，POST `/account/check-email`。
2. 已存在且有 `EmailPasswordHash` → 密碼登入；不存在或無密碼 → 舊站轉移頁。
3. 轉移頁提供舊站連結、使用者提供的原始教學圖與轉移碼（會員 ID）欄位。
   「沒有轉移碼，建立新帳號」會進入獨立註冊畫面，保留 Email、推薦碼與返回網址。
4. 同一 Email 只有一個會員。Google 與密碼可以並存，首次連結 Google 到密碼帳號需輸入原密碼。
   已登入的 Google 會員可在「我的消費記錄」設定初始密碼。

## 舊站轉移的邊界

輸入轉移碼後，以既有會員 ID 比對，轉移資格僅看 `HasLoggedInNewSite`。
ID 存在且標記為 false 就顯示帳號名稱；ID 不存在或標記為 true 則顯示「查無可轉移帳號」。不額外依角色、密碼或 Google 識別碼篩選。
使用者按「是，這是我的帳號」後設定密碼，沿用原會員 ID／名稱／消費紀錄、改為新 Email，清除舊 Google 識別碼並登入。
綁定成功會在同一交易內將 `HasLoggedInNewSite` 設為 true，前後台均不能再次轉移，也不能覆蓋其他 Email 帳號。
15 分鐘有效的加密票證綁定原資料及步驟；資料變更後必須重新查詢。
這是依使用者要求的 ID 自助轉移：知道未轉移會員 ID 的人就能提出轉移，名稱確認不構成帳號所有權驗證。

只有選「不是我的帳號」或送出 ID 後查無可轉移帳號，才出現客服及截圖上傳。
查無時標題為「查無可轉移帳號」，沒有額外「找不到」按鈕。
可直接聯絡客服，或先上傳 5 MB 內的 JPG／PNG／WebP；圖片經解碼、移除附加資訊並轉為 WebP，存於共用資料庫的 FrontendTransferSupport。
截圖上傳不會改變會員資料，也不會自動發送外部訊息。完成後提供申請編號，會員聯絡客服時提供該編號。
後台「前台帳號管理 → 帳號轉移客服申請」可查詢編號及查看截圖，沿用會員管理權限；無公開截圖網址。
客服確認歸屬後，可於後台「前台帳號 → 編輯 → 轉移舊站帳號」協助處理。
不新增轉移碼欄位，不加入 Apple 登入，不寄送 Email 驗證信。

## 部署

先備份資料庫，再由部署人員依序套用 `FrontendEmailPassword.sql`、`FrontendTransferSupport.sql`，
或套用 Admin migrations 至 `20261023000000_AddFrontendTransferSupport`（兩種方式擇一）。此變更沒有自行連線或修改共用資料庫。
先加欄位，再部署前後台；舊版應用可繼續使用新增欄位後的資料庫。
`PasswordHash` 因既有相容性繼續保存 Google sub，真正密碼存於新的 nullable `EmailPasswordHash`。
密碼使用 ASP.NET Core PasswordHasher，600,000 次迭代，不保存明文。
Cookie 依登入方式驗證；修改密碼雜湊或 Google sub 會撤銷對應舊 Cookie。

登入／註冊有 IP 與 Email 次數限制（5 分鐘視窗），Email 分流本身會透露是否已設定密碼，
這是此頁面的指定行為。限制目前存在單一程序記憶體，多實例部署需共用儲存。
未新增自助忘記密碼／已啟用帳號重設流程。截圖目前隨資料庫保留，沒有自動清理排程。

## 驗證

在前台 repo 執行 `dotnet run --project tools/GoogleLoginChecks/GoogleLoginChecks.csproj`。
在後台 repo 執行 `dotnet run --project tools/FrontendSettingsChecks/FrontendSettingsChecks.csproj`。
測試使用隔離的記憶體 SQLite，不讀取正式資料庫設定。
