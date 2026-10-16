# Roll & Pay — バックエンド

C# / .NET 10 / Azure Functions 4.x Isolated Worker。G01〜G06（認証・参加者取得・ゲーム状態取得・作成・中止・抽選・再抽選）、G07（金額プレビュー）、G08（金額確定・累積額）、G09（履歴参照）、G10（履歴削除）を実装。

## 現在の範囲

`GET /api/v1/me`（A01）は管理者のAPIアクセストークンで認証し、200 `{"authorized":true,"role":"administrator"}`を返す。本文・クエリの指定は認証後に400で拒否する。A01はDBに接続しない。

`GET /api/v1/participants`（A02）は本人のrosterをPoint Readし、有効な参加者をsortOrder→participantIdの順で返す。応答は`{"participants":[{"participantId":"p001","displayName":"参加者A","sortOrder":1}]}`。有効者が0〜1人でも200。本文・クエリは400、データ未配置・破損・読取セッションを満たせない場合は503。

`GET /api/v1/games/active`（A04）は未完了ゲームを`{"game":<Gameまたはnull>}`で返す。`GET /api/v1/games/{gameId}`（A05）は本人のゲームと現ラウンドだけを復元し、完了・中止も取得できる。不在・所有者外は404、削除済みは410。詳細は次節を参照。

`POST /api/v1/games/{gameId}/payment-preview`（A08）は`{"totalYen":13800}`から保存なしで負担額・負け額を計算する。敗者確定済みのawaitingPaymentだけで利用できる。詳細は「金額プレビュー（G07）」を参照。

`GET /api/v1/health`は認証なしで200と次のJSONを返す。

```json
{"status":"ok"}
```

`Cache-Control: no-store`を付与する。APIプロセスへの到達確認用で、DB接続やEntra IDの正常性を確認するreadinessチェックではない。環境名・バージョン・接続情報は返さない。

全14業務APIを実装済み（A11は欠番）。フロントエンドのAPI接続状況はfrontend/README.mdを参照。ヘルスチェックは匿名の運用エンドポイントで、全業務APIを管理者限定にする設計とは区別する。

## 履歴削除（G10）

`DELETE /api/v1/games/{gameId}`（A14）は管理者認証、`Idempotency-Key`、履歴詳細A15の単一の強い`If-Match`が必須。本文・クエリは指定できず400、Content-Typeは不要。キー／If-Match欠落428、不正形式400。不在／所有者外404、削除済み410、版不一致412、未完了・中止409の順に判定する。

completedのgameをdeletedへ変更し、revision加算とupdatedAt/deletedAt設定、保存済みpayment.lossYenだけの累積減算、成功commandの作成を同一所有者の3操作TransactionalBatchでコミットする。game/lossTotalsは内部ETag条件付きReplace。0円も削除可能。会計・完了日時・当時名・全ラウンドは保持し、controlは読取・更新しないため別の未完了ゲームへ影響しない。過去の計算規則を再適用しない。集計不足・不正値・欠落は503で全件未更新とし、自動修復しない。

200はgameId/status/revision/deletedAt/removedLoss/lossTotalsと更新後ETag・最新X-Read-Session・Idempotency-Replayedを返す。現在名・無効者を含む集計を返し、整数文字列を維持する。参加者マスターはDB設計どおり業務更新中に変更しない。HTTPメソッド・正規化パス・本文なし・元If-Matchのハッシュを照合し、同キー同要求は元の成功を再生、キー流用409。削除後の過去保存要求の再生も復活・再加算しない。再生応答の集計は当時値のため、A12で最新値を取得する。

別履歴削除・新ゲーム保存の集計競合は、元If-Matchを保持したまま最新集計から減算し直す。ゲーム版が変わった場合は追随せず410/412。最大3回バッチ・全体10秒。応答断は成功記録で照合し、確認できなければ503として元の要求の再送を維持する。削除後は詳細・共有結果・一覧キャッシュとカーソルを破棄し、A10を先頭から、A12を読取セッション付きで取得する。フロントエンド接続は別作業。

## ゲーム作成（G04）

`POST /api/v1/games`（A03）は管理者認証と`Idempotency-Key: <空でないUUIDのD形式>`が必須。本文は`{"participantIds":["p001","p002"]}`で、2〜6名の有効な固定参加者を選択順に指定する。キー欠落428、形式不正400、本文の値・未知／重複フィールド422、JSON以外415、本文16 KiB超過413。クエリ・A03では使用しないIf-Matchは400で拒否する。

201でrevision=1・rollingのGame、全員未抽選の第1ラウンド、公開ETag、`Location: /api/v1/games/{gameId}`、`Idempotency-Replayed: false`を返す。rosterから当時名を複写し、選択順を1から保存する。game/round1のCreate、内部ETag条件付きのcontrol/rosterのReplace、commandのCreateの5操作を同一所有者のバッチでコミットする。rosterの値は変えず、名前・有効状態の並行変更を検出する。

成功操作記録は業務検証より前に読む。HTTPメソッド・正規化した完全パス・正規化JSON・元If-MatchのSHA-256を比較し、同じ要求なら保存済みの201／完全な本文／ETag／Locationを返す。JSONの空白とプロパティ順を無視し、配列順は区別する。キーは大文字小文字を正規化する。再生時は`Idempotency-Replayed: true`で、現在のrosterやゲーム状態へ追随せず、DBを再更新しない。要求内容が異なる同キー使用は409 `IDEMPOTENCY_KEY_REUSED`。成功記録には内部ETag・読取セッション・requestIdを保存せず、TTLなしで保持する。

既存の未完了ゲームがある場合はcontrolとゲーム・現roundを整合性確認し、409 `ACTIVE_GAME_EXISTS`と`details[0].gameId`を返す。壊れたポインターは503で停止する。条件付きバッチの409/412は同キー記録の確認からやり直し、最大3回バッチを実行、要求全体10秒で打ち切る。参加者マスターが変わった場合も再検証する。SDKの依存失敗424を直接公開せず、実際の失敗を429/503または競合の再確認に変換する。

503の通信失敗後は成功記録を照会し、見つかれば再生する。要求期限に達した場合など確認できなければ503を返すため、クライアントは元のキーと本文を保持して再送する。タイムアウトを未コミットとは断定しない。DBの最新セッションは読取・バッチ・再生とも`X-Read-Session`へ継承する。

最大100名のroster（ID40文字・名前50文字）と6名選択の5保存ドキュメント合計が128 KiB以下であることを自動検証する。SDKのバッチ付帯情報を含む実要求サイズ・RU・時間枠は実Cosmosで未計測。

## ゲーム中止（G05）

`POST /api/v1/games/{gameId}/cancel`（A13）は管理者認証、`Idempotency-Key`、単一の強い`If-Match`、UTF-8のJSON本文`{}`が必須。キー／If-Match欠落428、形式不正・クエリ・不正gameIdは400。空本文や不正JSONは400、空オブジェクト以外・未知フィールド422、Content-Type不正415、16 KiB超過413。ゲームIDとキーはGUIDのD形式を小文字へ正規化する。

rolling/tie/awaitingPaymentからcancelledへ遷移し、revisionを一つ増やし、サーバー時刻をupdatedAt/cancelledAtへ保存する。出目・同点者・確定済み敗者・当時名・参加者順序・現ラウンドを保持し、paymentはnull。200のGameと新ETag、`Idempotency-Replayed: false`、最新`X-Read-Session`を返す。gameとcontrolの内部ETag条件付きReplace、成功commandのCreateを同一所有者の3操作バッチでコミットし、round・roster・lossTotalsは更新しない。

G04と共通の`CommandStore`で成功記録を先に照会し、同じメソッド・正規化パス・空オブジェクト・元If-Matchなら200／本文／ETagを再生する。再生時は`Idempotency-Replayed: true`。新しいゲームを開始した後でも元の中止結果を返し、新しいcontrolを解除しない。異なる対象・If-Matchや別操作での同キー使用は409 `IDEMPOTENCY_KEY_REUSED`。

記録がなければ不在／所有者外404、削除済み410、元If-Match不一致412、完了・中止済み409の順で判定する。game→round→control→game再確認で途中の更新を検知し、古い版へ自動追随しない。controlが欠落・別ゲーム・null、保存データ不整合は503で停止し、自動修復しない。内部ETag競合時は成功記録から再確認し、同キーは再生、ゲームが更新済みなら412。controlのみの競合は元のゲーム版を維持して最大3回バッチ、全体10秒以内で再試行する。コミット後の503通信断は成功記録で照合し、確認不能なら503として元のキー・If-Matchでの再送を維持する。金額保存APIとの競合確認はG08で追加する。

## サイコロ・再抽選（G06）

A06 `POST /api/v1/games/{gameId}/rolls` とA07 `POST /api/v1/games/{gameId}/rounds` は管理者認証、`Idempotency-Key`、単一の強いゲームETagの`If-Match`が必須。欠落428、不正ヘッダー・不正JSON・クエリ400、未知／重複フィールドや不正値422、JSON以外415、本文16 KiB超過413。ラウンド番号は正のJSON整数とする。

A06は`{"roundNumber":1,"mode":"individual","participantId":"p001"}`または`{"roundNumber":1,"mode":"remaining"}`。rollingの現ラウンドだけを対象に、暗号学的乱数で1〜6の出目を確定する。一括では未抽選者だけを振り、確定済みの値と日時を保持する。未抽選者がいれば判定せず、全員完了時に最小値が一名ならawaitingPaymentと敗者、複数ならtieと最小同点者を保存する。最小以外の同点は再抽選しない。200で更新後のGameを返す。

A07は`{"previousRoundNumber":1}`。tieかつ現番号一致のときだけ、参加者全員が未抽選の次ラウンドを作りrollingへ戻す。乱数は生成しない。旧ラウンドの全出目・日時・同点対象者を保持し、201 Gameと`Location: /api/v1/games/{gameId}`を返す。ラウンド回数について業務上の上限は設けず、全ラウンド取得APIは公開しない。

同じ所有者の3操作を条件付きバッチにまとめる。A06はroundとgameを内部ETag条件付きReplace、commandをCreate。A07は新roundとcommandをCreate、gameを条件付きReplaceする。control・roster・累積額は変更しない。公開revisionを一回加算し、ETag・最新の`X-Read-Session`・`Idempotency-Replayed`を返す。

成功commandを存在・削除・版・業務状態の検証より先に照会し、同じ要求は元のステータス・本文・ETag・Locationを再生する。別要求へのキー流用409、本人以外／不存在404、削除済み410、公開版不一致412、状態不正409、旧ラウンド409、抽選済み409、参加者外ID422。条件競合後も元If-Matchを保持し、競合した操作を新しい版へ自動適用しない。最大3試行・全体10秒。コミット後の応答断はcommandから確認し、確認不能なら503として同じ要求の再送を案内する。

`Games/GameDice.cs`の`IDie`は判定テストでのみ差し替える。本番は`RandomNumberGenerator.GetInt32(1, 7)`を使い、乱数を指定できるHTTP入力や設定は設けない。オフラインで判定・部分抽選・混在操作・旧ラウンド拒否・競合・再送・失敗時の未更新・HTTP契約を検証する。`tests/GameDiceIntegrationTests.cs`には実バッチの同時操作、全件ロールバック、応答断、別クライアントへの読取セッション継承の3テストを追加。実行入口は`database/tests/Test-CosmosIntegration.ps1`で、HTTPS loopbackのエミュレーターだけを使う。端末にエミュレーターがないため、実DBの原子性・RU・実テナント疎通は未検証。

## ゲーム状態の取得（G03）

A04/A05も管理者認証必須、本文・クエリなし。gameIdは空GUIDを除くハイフン付き36文字GUIDで、大文字は小文字に正規化してPoint Readする。不正形式は400 `INVALID_REQUEST`。固定パス`games/active`を詳細ルートより先に扱う。

A05はgame→現round→gameの順に読み、最後の内部ETagが最初と同じで、保存内容も整合する場合だけ応答する。更新が入った場合は25ミリ秒待機して取り直し、最大3試行・要求全体10秒で安定しなければ503 `TEMPORARILY_UNAVAILABLE`と`Retry-After: 5`。存在するゲームのround欠落や保存形式・状態の矛盾も503。DBの404/1002（セッション未達）を不存在として扱わず、通常の404ではコンテナーの存在も確認して、接続先DB・コンテナー欠落とゲーム不存在を区別する。

A04はcontrol→game→round→game再確認→control再確認を行う。ポインターがnullのときもcontrolを再確認する。controlのETagが変わったら再取得し、安定したポインターが不存在・完了・中止・削除済みゲームを指す場合は503。自動解除やDB修復はしない。未完了として返すのはrolling/tie/awaitingPaymentのみ。

Gameと現RoundのID・版・所有者、保存時の参加者・順序、出目・日時・敗者・会計の整合性を検証する。当時名や保存会計を返し、現在のrosterや計算規則で上書きしない。schemaVersion=1のdeletedAt欠落はnullとして読み、削除済みはGameを公開しない。全ラウンドの走査や更新処理は行わない。

Gameがある応答は`ETag: "g-<gameId>-v<revision>"`を返す。内部`_etag`と区別した単一の強いETagで、A04のgame=nullやエラーには付けない。応答にownerKey/type/schemaVersion/deletedAt等の保存用フィールドを含めない。G02の読取セッションを各Point Readで引き継ぎ、最後の読取で得た値を`X-Read-Session`に返す。

## 金額プレビュー（G07）

A08も管理者認証必須。Content-TypeはUTF-8の`application/json`、本文は16 KiB以下で`totalYen`一項目のみ。1〜9,999,999円のJSON整数を受け付け、文字列・小数（`13800.0`を含む）・指数表記・0・負数・上限超過・未知／重複フィールドは422。不正JSONは400、クエリと不正gameIdは400、Content-Type不正は415、本文超過はContent-Lengthなしでも413。Idempotency-Key・If-Matchは不要。

G03のGameReaderを使って本人の保存ゲーム・現ラウンドを整合する版で読み、保存人数・保存敗者を使う。rolling/tie/completed/cancelledは409 `INVALID_GAME_STATE`、不在・所有者外は404、削除済みは410。保存状態の不整合・継続競合は503。roster・control・lossTotals・成功操作記録を更新しない。

`Payments/PaymentRequest.cs`は本文検証、`Payments/PaymentCalculator.cs`はG08でも利用する計算処理。64ビット整数で、人数N・総額TからB=`(T / (N * 1000)) * 1000`、L=`T - B * N`を計算し、敗者B+L・他者B・受取B×(N−1)を返す。負担合計=Tと0≤L<N×1000を検証。13,800円・4人ではB=3,000円、L=1,800円、敗者4,800円、受取9,000円。小額時のB=0、L=0も許可。

200応答は`{"gameId":"<UUID>","revision":4,"payment":<Payment>}`。Paymentは既存の公開形式で`calculationVersion="thousand-remainder-v1"`、`registeredAt=null`。参照したゲームrevisionの強いETagと最新の`X-Read-Session`を返す。プレビュー後も保存状態・revisionは変わらない。G08では同じ総額とETagを受け取り、サーバーで再計算して確定日時を付ける。

## 履歴の参照（G09）

A10 `GET /api/v1/games`は本文なし、`limit`（1〜50、既定20）と`cursor`だけを受け付ける。本人のパーティション内のcompletedだけをcreatedAt降順→gameId降順で返す。敗者名は保存当時の名前。0円履歴も含み、空は`{"items":[],"nextCursor":null}`。一覧に全参加者・出目・基準負担額は含めない。

境界カーソルはversion・UTCミリ秒日時・gameId・limitのJSONをbase64url化した4 KiB以下の不透明値。次要求では同じlimitとnextCursorを渡す。未知・重複クエリや不正limitは400 `INVALID_REQUEST`、カーソルの形式・値・limit不一致は400 `INVALID_CURSOR`。署名は付けず、改変しても本人の履歴の境界だけが変わる。保存・削除をまたぐ厳密な一覧スナップショットは保証せず、削除後は先頭から取り直す。

Cosmosの既存複合インデックス（type ASC/status ASC/createdAt DESC/gameId DESC）を使い、TOP limit+1をパラメーター化したクエリで取得する。SDKの短いページ・空ページを終端まで読み、余分な一件があれば最後の返却行をnextCursorにする。必要な保存ゲーム項目だけを投影し、所有者・日時・参加者・保存会計の整合性を検証する。roster・round・commandは読まない。

A15 `GET /api/v1/games/{gameId}/history-detail`は本文・クエリなし。A05と同じGUID検証を行い、game一件のPoint Readだけで当時名・参加順・敗者・保存済みpayment・完了日時・canDelete=trueを返す。公開ETagはA05と同じゲームrevisionから生成する。不存在・所有者外404、削除済み410、未完了・中止409 `INVALID_GAME_STATE`。抽選・出目・ラウンド数を応答しない。現在の参加者名や計算規則で過去会計を再計算しない。

両APIは共通の管理者認証・読取セッション・no-store・エラー変換を使用する。DB読取全体は10秒以内、空一覧でもX-Read-Sessionを返す。セッション未達・DB/コンテナー欠落・保存データ破損は503とRetry-After、DBの429は429に変換し、古いセッションや空履歴で成功扱いにしない。

## 認証・HTTP共通処理

全HTTP関数にWorkerミドルウェアを適用し、GETのHealth関数とOPTIONSだけを明示的に認証対象から除外する。`Authorization: Bearer <API access token>`が必須。Microsoft IdentityModelでテナント固有のHTTPS OpenID Connectメタデータ・JWKSを取得し、キャッシュと署名鍵更新を行う。RS256署名、発行者、API Client IDのaud、有効期限（許容時刻差30秒）、v2トークンを検証した後、tid・管理者oid・SPAのazp・委任scpを確認する。メールアドレスやクライアント指定ユーザーIDで認可しない。IDトークン、Graph向けトークン、アプリ単独トークンは許可しない。

設定名は既存の`infra/resources.bicep`と共通。`local.settings.json`のValuesでは次を指定する。IDはすべて空でないGUID、APIとSPAは別登録とする。

| 設定 | 値 |
|---|---|
| `Auth__TenantId` | 対象のEntraテナントID |
| `Auth__ApiClientId` | APIアプリのClient ID（v2トークンのaud） |
| `Auth__AllowedSpaClientId` | 許可するSPAアプリのClient ID |
| `Auth__AdministratorObjectId` | 対象テナント内の管理者ユーザーObject ID |
| `Auth__RequiredScope` | `access_as_user` |

クライアントシークレットは不要。ローカルでも実際のEntra APIアクセストークンを用いる。認証を無効化する環境設定や開発用トークンは実装しない。設定不備や初回メタデータ取得失敗は業務APIを503（`Retry-After: 5`）で停止させ、ヘルスチェックは維持する。メタデータHTTP取得のタイムアウトは10秒。設定変更後はプロセスを再起動する。

欠落・不正・期限切れは401 `UNAUTHENTICATED`と`WWW-Authenticate: Bearer`、有効トークンの管理者・SPA・スコープ等の不足は403 `FORBIDDEN`。認証・認可に失敗した要求は関数本体やDB処理を呼ばない。エラーはAPI定義の`error.code/message/details/requestId`を返し、requestIdは要求ごとにサーバーで生成する。内部例外・トークン・個人情報を応答やアプリログに出さない。

全応答に`Cache-Control: no-store`と`Access-Control-Expose-Headers: ETag, Location, Idempotency-Replayed, Retry-After, X-Read-Session`を設定する。Originの許可と`Access-Control-Allow-Origin`はFunctionsホストのCORS設定に任せ、APIで重複設定しない。本番は既存BicepのSWA Origin完全一致を維持する。ローカル設定例のHost.CORSは`http://localhost:5173`のみ。実際の開発Originに合わせてローカルファイルを編集する。`OPTIONS /api/v1/{*path}`は認証・業務処理を呼ばず204を返し、GET/POST/DELETE/OPTIONSとAPI定義の要求ヘッダーを提示する。CORSは認証の代わりにはならない。

## Cosmos DB接続と読取セッション

既存インフラの`Cosmos__Endpoint`（HTTPSアカウントURL）、`Cosmos__DatabaseName`、`Cosmos__ContainerName`を読み込む。設定例は`rollpay/app`。APIにDB・コンテナー・初期データの作成処理はない。DB設定や接続が不完全でもhealth・meは利用でき、DBを使うA02/A04/A05/A10/A15が503になる。

本番はシステム割当Managed Identity、Development環境はAzure CLIのログインIDで認証する。キー・接続文字列・クライアントシークレットは使わない。ローカルで実DBを読む場合は`az login --tenant <対象テナントID>`し、接続先を明示する。API起動時には接続せず、初回のDB利用時にクライアントを作り、Functionsプロセス内で再利用する。既定の本番接続先は設けない。

全Point Readで、検証済みtid・oidをGUIDの小文字D形式に正規化した`<tid>:<oid>`をPartitionKeyに指定する。クエリや追加ヘッダーのownerKeyを採用しない。rosterのid/type/schemaVersion/ownerKey、参加者ID・重複・人数・名前・順序・UTC日時も検証する。APIが返すのは公開フィールドだけで、内部ETagは公開しない。

SDKはGateway接続・Session整合性。SDK要求のタイムアウト5秒、429の追加再試行は最大2回・合計待機3秒、A02のDB処理全体に10秒の上限を置く。429は`RATE_LIMITED`、タイムアウト・データ未配置・権限不足・セッション未達は`TEMPORARILY_UNAVAILABLE`に変換し、Retry-After秒を返す。接続先・SDK内部診断・参加者名をエラーやログに出さない。

`X-Read-Session`はSDKのSessionTokenをUTF-8→base64url（パディングなし）にした不透明値。認証後に単一ヘッダー・8 KiB以下・正規base64url・正しいUTF-8・制御文字なしを確認し、不正なら400 `INVALID_READ_SESSION`。A02は復元値をSDKのSessionTokenへ渡し、成功読取で得た最新値を応答に返す。別インスタンスへの次要求でもこの値を渡す。無効なSDKセッションは400、セッションを満たせない読取は503とし、セッションなしで読み直して成功扱いにはしない。DB未使用のA01では応答セッションを省略する。

初期3ドキュメントの登録とローカルエミュレーター統合検証は[databaseの手順](../database/README.md)を参照。本番への登録・Azure配置はコードのPR作成とは別作業。

## 金額確定・累積額（G08）

`POST /api/v1/games/{gameId}/payment`（A09）は`{"totalYen":13800}`、`Idempotency-Key`とプレビューで得た`If-Match`を受け取る。G07の厳密な整数検証・計算を再利用し、awaitingPaymentからcompletedへ遷移する。`registeredAt`・`completedAt`・`updatedAt`は同じサーバー時刻。200は`{"game":<Game>,"lossTotals":<A12の応答>}`と新しいゲームETagを返す。

game・control・lossTotalsは内部ETag条件付きReplace、成功commandはCreateとして、同一所有者の4操作TransactionalBatchで保存する。敗者の累積へ保存するLだけをcheckedな64ビット加算で反映し、他の参加者の額・当時名・出目・ラウンドを保持する。0円の負け額も確定できる。保存済み総額の再編集はできない。

成功記録を現在のゲーム状態より先に確認し、同キー・同じ要求・元If-Matchでは元の本文・ETagを再生して再加算しない。別金額・別版・別対象・別操作へのキー流用は409。削除後の保存成功応答の再生もDBを更新しない。不在／所有者外404、削除済み410、版不一致412、awaitingPayment以外409。内部ETag競合では元ゲーム版を維持して最大3回バッチ・全体10秒で打ち切る。集計だけの競合では最新集計から再計算する。コミット後の応答断は成功記録で確認し、不明なら元キーで再送できる503を返す。

`GET /api/v1/statistics/losses`（A12）は本文・クエリなし。現在のroster全員（無効者を含む）をsortOrder→participantId順で返し、現在名・isActive・10進整数文字列`totalLossYen`を含む。集計は最大100件で、初期値は全員`"0"`・`updatedAt=null`。roster→lossTotals→rosterのPoint Readでマスター世代変更を検出し、最大3回で整合する組を取得する。ゲームの走査や再計算は行わず、完了時に反映した集計を読む。未完了・中止は加算されない。削除時の減算はG10の担当。

集計・参加者の欠落や重複、不正な額、所有者・型・版・日時の不整合、ゲーム参加者と固定リストの不一致、累積オーバーフローは503で停止する。自動初期化・修復はしない。A09の現在名はDB設計どおり、業務更新中のマスター変更禁止を前提とする。A09直後は応答の集計を表示し、次のGETには最新`X-Read-Session`を継承する。再生応答は過去時点なので、ゲームと集計を再取得する。

G08では`LossTotalsDocument.updatedAt`をnullableにし、新しい初期登録はnullを保存する。以前の登録ツールが保存した初期日時も読み取れ、その日時を維持する。ゼロ額だけでは未保存と0円確定済みを区別できないため、既存データを自動でnullへ書き換えない。
## 初回の環境準備（Windows）

必要なもの：.NET 10 SDK、.NET 10に対応するAzure Functions Core Tools v4。

通常のPowerShellでインストールする場合：

```powershell
winget install --exact --id Microsoft.DotNet.SDK.10
winget install --exact --id Microsoft.Azure.FunctionsCoreTools
```

インストール後はターミナルを開き直す。VS CodeのターミナルならVS Codeも再起動する。

```powershell
dotnet --list-sdks
func --version
```

`global.json`は10.0.100以上の安定版.NET 10 SDKを選ぶ（同じメジャー・マイナー内で最新feature bandへロールフォワード）。SDKはFunctionsランタイムとは別に必要。

## ビルド・ローカル起動

リポジトリのルートから：

```powershell
cd backend
Copy-Item local.settings.example.json local.settings.json
dotnet restore RollAndPay.Api.csproj
dotnet build RollAndPay.Api.csproj --no-restore
dotnet run
```

設定のコピーは初回だけ。上記のAuth設定を実際のIDに置き換える。`local.settings.json`はGit管理・発行対象から除外している。HTTPトリガーの起動・匿名ヘルスチェックにAzureへのログイン、Cosmos DB、Azuriteは不要。認証成功の確認にはMSAL等で取得した対象APIの委任アクセストークンとEntraへのメタデータ取得通信が必要。Core ToolsはStorage未設定の内部ヘルス警告を出すことがあるが、HTTPエンドポイントのヘルスチェックとは別である。

別ターミナルで確認する：

```powershell
curl.exe -i http://localhost:7071/api/v1/health
curl.exe -i http://localhost:7071/api/v1/me
# MSALでAPI向けトークンを取得し、$accessTokenに保持した場合：
curl.exe -i http://localhost:7071/api/v1/me -H "Authorization: Bearer $accessToken"
curl.exe -i http://localhost:7071/api/v1/participants -H "Authorization: Bearer $accessToken"
```

healthの200、JSONの`status: ok`、`Cache-Control: no-store`、トークンなしmeの401を確認する。有効な管理者トークンならmeは200。終了は起動ターミナルでCtrl+C。NuGetパッケージの初回取得にはネットワーク接続が必要。

## テスト

```powershell
cd backend
dotnet test tests/RollAndPay.Api.Tests.csproj --configuration Release
```

オフラインテストではテスト専用RSA鍵によるJWTを使い、認証・認可、A01・A02、エラー形式、DBの未呼出し、所有者分離、セッション継承、初期登録の検証と3件Createバッチを確認する。SDKのContainer等をモックするテストは実DBの原子性の証明には含めない。localhostのCosmosエミュレーター専用の統合2件は通常はスキップし、[databaseの実行入口](../database/README.md)から明示的に有効化する。テストの差替えはDI内だけで行い、本番の認証回避設定は設けない。テストと登録ツールはAPIの発行物から除外し、CIの`pipeline/backend/Build.ps1`でも確認する。

2026年10月16日：49テスト成功、Releaseビルド・発行成功、発行物へのローカル設定・テスト資材の混入なし。Core Toolsの実ホストでhealth 200・me 401・OPTIONS 204、ローカルCORSのOrigin許可・拒否、401での応答ヘッダー公開を確認。実テナントの管理者トークンによる疎通、Azure上のOrigin許可・拒否とプリフライトは未検証。Azureへの配置はこの作業に含まない。

G02追加後：オフライン103テスト・Release発行成功、実Functionsホストでhealth 200・participants 401・OPTIONS 204を確認。エミュレーター未導入のため統合2件はスキップし、実Cosmosでの読取・一括登録・SessionToken継承・Managed Identity/RBACは未検証。本番へのデータ登録・Azure配置は未実施。

G03追加後：オフライン168テスト成功。G03の2件を含むエミュレーター統合4件は未導入のためスキップ。実FunctionsホストでA04/A05のルート、activeがActiveGame関数へ到達すること、認証なし401、health 200、OPTIONS 204を確認。実Cosmosでの復元・並行更新・権限の受入確認は未実施。統合テストは`database/tests/Test-CosmosIntegration.ps1`で実行する。

G04追加後：オフライン214テスト成功、Release発行成功、ローカル設定・テスト・初期登録ツールの発行混入なし。G04で追加した統合3件を含む計7件はエミュレーター未導入のためスキップ。G04の統合テストは同キー／別キー同時作成、rosterのETag競合による全件ロールバック、コミット後応答断、別クライアントのセッション継承と再生を実DBで確認する内容で、既存の実行入口を使う。モック／メモリー上の成功を実DBの原子性の証明として扱わない。Managed Identity/RBAC・実テナントのトークンによるA03成功疎通は未実施。

実Functionsホストでも`CreateGame: [POST] /api/v1/games`の登録、POSTの認証なし401と共通応答ヘッダー、health 200、OPTIONS 204を確認。Azure配置・本番データへの書込は実施していない。

G07追加後：`pipeline/backend/Build.ps1`でオフライン234テスト成功（G07追加66件）、Release発行と発行物検査成功。計算例・2〜6人と敗者全位置・金額境界、厳密な整数入力、本文上限、認証／所有者／状態制限、読取セッション継承、途中更新後のETag／状態再確認、DB読取のみを検証。実FunctionsホストでPaymentPreviewルートへの到達と認証なし401・health 200・OPTIONS 204を確認。既存エミュレーター統合4件は未実施で、実Cosmos・実テナントトークンによるA08の疎通も未検証。作成→抽選→プレビュー→保存の一連検証はG08で実施する。

G04を含むmainのマージ後：`pipeline/backend/Build.ps1`でG04・G07を含むオフライン280テスト成功、エミュレーター統合7件はスキップ。Release発行と発行物検査も成功。

G09追加後：オフライン256テスト成功、Release発行・発行物検査成功。実FunctionsホストでHistoryList/HistoryDetailへの到達、認証なし401、health 200、OPTIONS 204とno-store・応答ヘッダー公開を確認。G09の2件を含むエミュレーター統合6件は未導入のためスキップ。実CosmosのSQL・複合インデックス適用・RU・別インスタンス間のSessionToken、および実テナントの管理者トークンによるA10/A15の疎通は未検証。

G04のmainマージ取り込み後：オフライン302テスト成功、Release発行・発行物検査成功。G04のゲーム作成とG09の履歴参照を両方登録し、関連資料を統合。エミュレーター未導入のため統合9件は未実施（スキップ）。

G07のmainマージ取り込み後：オフライン368テスト成功、Release発行・発行物検査成功。金額プレビューと履歴参照の資料を統合。エミュレーター未導入のため統合9件は未実施（スキップ）。

G05追加後（G09マージ済みmainへリベース）：`pipeline/backend/Build.ps1`でオフライン430テスト成功（G05追加62件）、Release発行・発行物検査成功。未完了3状態、出目・敗者保持、ポインター解除、同キー再送・別キー競合・キー使い回し、読取中の更新、条件競合の打切り、応答断、DB更新前の失敗、新ゲーム開始後の過去キー再送、セッション継承、HTTPの入力・認証境界を確認。実FunctionsホストでCancelGameルートと関数への到達、認証なし401、health 200、OPTIONS 204、共通応答ヘッダーを確認。エミュレーター未導入のためG05追加3件を含む統合12件は理由付きスキップ。実Cosmosの原子性・ETag・セッション、Managed Identity/RBAC、実テナントのトークンによるA13成功疎通は未検証。保存／中止の競合確認はG08で追加する。

2026年10月16日（G06）：G09（PR #6）マージ後のmainへリベースし、オフライン426テスト（G06追加58件）、Release発行・発行物検査に成功。Core Toolsの実FunctionsホストでRollDice/CreateRoundのPOSTルート、認証なし401、health 200、no-storeを確認。エミュレーター未導入のため統合12件（G06追加3件）は未実施（スキップ）。Azureへの配置・実データへの書込は実施していない。

G05・フロントエンドAPI接続のmainマージ後：共通If-Match検証をG05の実装に統合。形式が正しい強いETagの不一致は412とし、抽選／再抽選と中止が競合した場合に双方の勝者を保持する4ケースを追加。`pipeline/backend/Build.ps1`でオフライン495テスト成功、Release発行・発行物検査成功。エミュレーター未導入のため統合15件は理由付きスキップ。

2026年10月17日（G08）：`pipeline/backend/Build.ps1`でオフライン594テスト（G08追加99件）が成功し、Release発行・発行物検査も成功。作成→同点→全員再抽選→13,800円・4人のプレビュー→保存→再送→累積額取得を認証付きHTTPテストで確認。0円、JavaScript安全整数超の累積額、オーバーフロー拒否、現在名・無効参加者、保存／中止の両勝者、条件競合、応答断、削除後の過去保存キー再生を検証した。実FunctionsホストでSavePayment/GetLossTotalsのルートと認証なし401、health 200、OPTIONS 204、no-storeを確認。エミュレーター未導入のため統合19件（G08追加4件）は理由付きスキップ。追加統合テストは実バッチの同キー／別キー同時保存、4操作それぞれの競合時ロールバック、保存／中止、応答断、別クライアントでのセッション継承・再生・集計読取を扱う。実Cosmosの原子性・RU・要求サイズ・時間枠、実テナントトークンでの成功疎通は未検証。Azure配置・本番データ書込は行っていない。
2026年10月17日（G10）：`pipeline/backend/Build.ps1`でオフライン646件（G10追加52件）・Release発行・発行物検査が成功。二重削除、0円・最終一件、保存された過去計算額の減算、別履歴削除・新ゲーム保存の競合、負の集計の停止、応答断、別の未完了ポインター保持、削除後の古い保存要求の再生を検証。認証付きHTTPで作成→同点→全員再抽選→プレビュー→保存→削除→過去保存再送→累積0を確認し、別テストで詳細ETag→削除→履歴空・詳細／共有結果410・累積0を確認。実FunctionsホストでDeleteHistoryのDELETEルートと認証なし401、health 200、OPTIONS 204、no-store・応答ヘッダー公開を確認。エミュレーター未導入のため統合23件（G10追加4件）は理由付きスキップ。実Cosmosの原子性・RU・実テナントトークンでの成功疎通は未検証。Azure配置・本番データ書込は行っていない。

## ファイル構成

- `Program.cs`：FunctionsホストとHTTP連携の初期化。
- `Functions/HealthFunction.cs`：ヘルスチェック。
- `Functions/MeFunction.cs`／`OptionsFunction.cs`：A01とプリフライト。
- `Functions/ParticipantsFunction.cs`：A02。
- `Functions/GameReadFunctions.cs`：A04・A05。
- `Functions/GameCreateFunction.cs`：A03。
- `Functions/GameCancelFunction.cs`：A13。
- `Functions/GameDiceFunctions.cs`／`Games/GameDice.cs`：A06・A07、サーバー乱数、条件付き更新と成功応答の再生。
- `Functions/PaymentPreviewFunction.cs`：A08。
- `Functions/PaymentSaveFunction.cs`／`Functions/LossTotalsFunction.cs`：A09・A12。
- `Payments/`：金額検証・計算・確定、集計の検証・現在名との組立。
- `Functions/HistoryReadFunctions.cs`／`History/`：A10・A15、カーソルと履歴応答。
- `Functions/HistoryDeleteFunction.cs`／`History/HistoryDeleter.cs`：A14、論理削除・保存L減算・成功記録の一括保存と再生。
- `Data/CosmosHistoryStore.cs`：所有者パーティション内の履歴クエリ。
- `Games/`：読取の整合性確認、保存状態の検証、公開Game/RoundとETag。
- `Authentication/`：認証設定、JWT検証と管理者認可。
- `Data/`：Cosmosクライアント・保存モデル・所有者識別・rosterのPoint Read。
- `Http/`：全HTTP関数に適用するWorkerミドルウェア、共通パイプラインとエラー。
- `Http/UpdateRequest.cs`／`Data/CosmosBatchWriter.cs`：更新要求の形式・ハッシュ、条件付きバッチとSDKエラー変換。G04で必要な範囲だけを実装し、後続の更新APIは各グループで追加する。
- `tests/`：認証・HTTPの自動テスト（発行対象外）。
- `RollAndPay.Api.csproj`：SDKと依存パッケージ。
- `host.json`：Functionsホスト設定。
- `global.json`：.NET SDKの選択。
- `local.settings.example.json`：秘密情報を含まないローカル設定例。

設計は[API定義](../spec/api-design.md)・[インフラ設計](../spec/infrastructure.md)を参照。

## GitHub Actionsでの配置

バックエンド資産がmainに入ったときだけ、.NET 10のテスト・発行後に既存Flex Consumptionへ自動配置する。フロントエンドとは別のワークフロー。ローカル設定ファイル・設定例・テストは発行物から除外する。GitHubとAzureのOIDC準備、Repository Variablesは [pipelineの手順](../pipeline/README.md) を参照。配置対象は上記エンドポイントと認証・HTTP共通処理。

参考：[Isolated Worker公式ガイド](https://learn.microsoft.com/en-us/azure/azure-functions/dotnet-isolated-process-guide)、[Core Tools](https://github.com/Azure/azure-functions-core-tools)、[HTTPトリガーのローカル設定](https://learn.microsoft.com/en-us/azure/azure-functions/functions-develop-vs?pivots=isolated)。
