# Roll & Pay — API定義

作成日：2026年10月14日

状態：基本設計改訂稿。2026年10月14日の画面変更（履歴分離・負け額中心・F14履歴削除）を反映。[機能詳細](functional-design.md)のB01〜B10を採用する提案。実装・実環境検証は未実施。

参照：[要件整理](../docs/requirements.md)／[DB設計](database-design.md)／[インフラ](infrastructure.md)

## 1. 共通仕様

### 運用用ヘルスチェック（ベースライン実装）

`GET /api/v1/health`は認証不要、200 `{"status":"ok"}`と`Cache-Control: no-store`を返す。APIプロセスの応答のみを確認し、DB・認証基盤への疎通は判定しない。業務データ・環境情報を返さない。以下の管理者認証・冪等性・DBセッションの共通仕様は業務APIに適用し、このヘルスチェックには適用しない。G01で共通認証・HTTP処理とA01、G02でDB接続・読取セッションとA02、G03でゲーム状態取得A04・A05、G07で金額プレビューA08を実装。A03・A06・A07・A09以降の業務API（A11欠番）は未実装。

Base URL：`https://<function-host>.azurewebsites.net/api/v1`。以下は相対パス。HTTPS、UTF-8、JSON、camelCase。全業務APIは管理者のみ。ログイン・ログアウトはMSALで行い、独自ログインAPIは作らない。

### 認証・データ形式

- `Authorization: Bearer <API access token>`必須。インフラ第5節の条件を全APIに適用。認証→認可→形式検証→業務検証→DB更新の順。
- 本文ありは`Content-Type: application/json`、16 KiB以下。他形式415、超過413、不正JSON400、値域・未知フィールド422。
- ゲームIDはサーバー生成UUID。参加者IDは安定した固定ID（例p001）。所有者、出目、敗者、計算結果、日時はサーバーが決める。
- 金額入力は1〜9,999,999円のJSON整数。文字列、小数、指数表記も拒否。演算は64ビット整数。
- 日時はUTCのRFC 3339。未確定はnull。参加者は選択順、ラウンドは番号昇順。
- 応答は`Cache-Control: no-store`。本文・トークン・名前・継続トークン・読取セッションをログに出さない。
- 未記載のクエリは400。ID形式不正は400、適正形式だが存在しないゲームは404。

### 更新・競合・再送

A03/A06/A07/A09/A13/A14は`Idempotency-Key`必須（クライアント生成UUID、管理者全体で一意）。A06/A07/A09/A13/A14はゲーム応答または履歴詳細A15の`ETag`を`If-Match`に指定する。欠落428。If-Matchは単一の強いETagのみ、ワイルドカード・複数指定・不正形式は400。

公開ETagはゲームIDとrevisionで作る（例：`"g-7c56e8d0-1a58-4c45-8ef3-6505ad5c0a01-v2"`）。DB内部の`_etag`とは別。公開revisionはゲーム更新ごとに増やし、DB置換には読取時の内部ETagを条件指定する。

認証・形式検証後、成功操作記録を先に調べる。同一キー・HTTPメソッド・パス・正規化本文・元のIf-Matchなら元のステータス／本文／ETagを再生する。現在状態が変化していても再生する。異なる要求のキー再利用は409 `IDEMPOTENCY_KEY_REUSED`。記録はTTLなし。4xx/5xxは恒久記録しない。

記録がなければ存在・所有者・削除状態を検証。本人の削除済みゲームは410、存在しない／所有者外は404。その後revisionを検証し、不一致は412、状態不正は409。更新と成功記録は同一トランザクション。競合時も同じ順で再確認する。コミット後だけ成功応答する。

再生時`Idempotency-Replayed: true`、通常false。再生応答は過去時点の結果なのでフロントは最新ゲーム・集計を取得し、画面を古いrevisionへ戻さない。削除後の保存成功応答の再生でもDBを復活・再加算しない。最新取得が410なら表示から除外する。削除応答のlossTotalsも再生時は古い場合があるためA12を再取得する。requestId・読取セッションなどの輸送情報は再生時に変わってよい。

### 読取セッションとCORS

DBはSession整合性。Functionsの別インスタンスではSDKのセッションキャッシュを共有できないため、DBアクセス後の応答に`X-Read-Session`を返し、端末は以後の要求へ同名ヘッダーを付ける。SDKのコンテナー用SessionTokenをUTF-8→base64urlにした不透明文字列で、APIは復元してSDKのSessionTokenオプションに渡す。DB未使用のA01は省略可。

8 KiB以下、形式不正400。セッション指定による読取を満たせない場合は503で再試行を案内し、古い値を成功として返さない。これはアクセス資格ではなく、所有者は常に認証クレームから決める。最新応答の値で更新し、端末の更新要求は直列化。sessionStorageに保持し、ログアウト時削除。バックアップ復元後の古いセッションのリセットは運用手順に従う。

初回ログインやセッション情報を失った直後は以前の別セッションの最新書込が即時に見える保証を置かず、短い待機と再取得を行う。更新の安全性はDB条件付きトランザクションで守る。[SessionTokenの継承](https://learn.microsoft.com/en-us/azure/cosmos-db/how-to-manage-consistency)

CORSはインフラの完全一致Origin。許可要求ヘッダー：`Authorization, Content-Type, Idempotency-Key, If-Match, X-Read-Session`。公開応答ヘッダー：`ETag, Location, Idempotency-Replayed, Retry-After, X-Read-Session`。メソッドGET/POST/DELETE/OPTIONS。OPTIONSで業務処理をしない。

### 共通エラー

```json
{
  "error": {
    "code": "VALIDATION_ERROR",
    "message": "入力値を確認してください。",
    "details": [{"field":"totalYen","reason":"OUT_OF_RANGE"}],
    "requestId": "a3f5f44a-69cf-40c1-bd2f-df41c1cb6c47"
  }
}
```

詳細なしはdetails=[]。UIはcodeで分岐。内部DBエラー・スタック・名前は返さない。

| HTTP | code例 | 対応 |
|---|---|---|
| 400 | INVALID_REQUEST / INVALID_CURSOR / INVALID_READ_SESSION | 形式修正 |
| 401 | UNAUTHENTICATED | トークン再取得。WWW-Authenticate: Bearer |
| 403 | FORBIDDEN | 利用不可 |
| 404 | GAME_NOT_FOUND | 存在しない／所有者外も同じ応答 |
| 409 | ACTIVE_GAME_EXISTS / INVALID_GAME_STATE / ROUND_MISMATCH / ALREADY_ROLLED / IDEMPOTENCY_KEY_REUSED | 現在状態確認 |
| 410 | GAME_DELETED | 削除済みとして表示・端末キャッシュから除外、一覧・集計を再取得 |
| 412 | VERSION_MISMATCH | 最新取得。古い操作を自動実行しない |
| 413 / 415 | PAYLOAD_TOO_LARGE / UNSUPPORTED_MEDIA_TYPE | 本文・Content-Type修正 |
| 422 | VALIDATION_ERROR | 入力修正 |
| 428 | PRECONDITION_REQUIRED | キー／If-Match用意 |
| 429 | RATE_LIMITED | Retry-After秒後に同一要求再送 |
| 503 | TEMPORARILY_UNAVAILABLE | Retry-After秒後に同一要求再送 |
| 500 | INTERNAL_ERROR | 結果未確認としてキー保持 |

ACTIVE_GAME_EXISTSのdetailsは`[{"field":"gameId","reason":"ACTIVE_GAME_EXISTS","gameId":"<UUID>"}]`。DBの429はSDKで制限付き再試行し、時間枠超過は429。タイムアウト・503でも更新未成立とは断定しない。API時間枠・再試行回数は実装時に計測して設定し、無限再試行しない。

## 2. 共通応答モデル

以下は2人で一人目だけ抽選済みのGameの完全例。

```json
{
  "gameId":"7c56e8d0-1a58-4c45-8ef3-6505ad5c0a01",
  "revision":2,
  "status":"rolling",
  "createdAt":"2026-10-14T05:00:00Z",
  "updatedAt":"2026-10-14T05:00:10Z",
  "completedAt":null,
  "cancelledAt":null,
  "participants":[
    {"participantId":"p001","displayName":"参加者A","order":1},
    {"participantId":"p002","displayName":"参加者B","order":2}
  ],
  "currentRound":{
    "roundNumber":1,
    "status":"open",
    "createdAt":"2026-10-14T05:00:00Z",
    "completedAt":null,
    "rolls":[
      {"participantId":"p001","value":2,"rolledAt":"2026-10-14T05:00:10Z"},
      {"participantId":"p002","value":null,"rolledAt":null}
    ],
    "minimumValue":null,
    "tiedParticipantIds":[]
  },
  "loserParticipantId":null,
  "payment":null
}
```

Gameを返す応答にはゲームETagを付ける。通常取得の状態はrolling/tie/awaitingPayment/completed/cancelledの5種類。削除後はGameを返さず410とする。Round状態はopen/tied/decided。minimumValueは全員抽選完了時のみ数値、tiedParticipantIdsはtied時のみ最小同点者。それ以外は空配列。中止の場合は中止前の抽選情報・確定済み敗者を保持する。

Paymentは以下の形（4人・13,800円の独立した計算例）。Game.paymentは保存後にこの値を持つ。

```json
{
  "totalYen":13800,
  "participantCount":4,
  "baseYen":3000,
  "lossYen":1800,
  "loserParticipantId":"p001",
  "loserPaymentYen":4800,
  "collectFromOthersYen":9000,
  "shares":[
    {"participantId":"p001","amountYen":4800},
    {"participantId":"p002","amountYen":3000},
    {"participantId":"p003","amountYen":3000},
    {"participantId":"p004","amountYen":3000}
  ],
  "calculationVersion":"thousand-remainder-v1",
  "registeredAt":"2026-10-14T05:10:00Z"
}
```

プレビューのregisteredAtはnull。単一ゲーム金額はJSON数値。累積額はJavaScript安全整数範囲を超えないようAPIでは10進整数文字列（例`"1800"`）。保存名は1〜50文字、固定参加者IDは英数字・ハイフン・アンダースコアの1〜40文字。名前はAPI入力しない。

## 3. API一覧・機能対応

| ID | メソッド・パス | 用途 | 機能 |
|---|---|---|---|
| A01 | GET /me | 利用権限確認 | F01 |
| A02 | GET /participants | 固定参加者取得 | F02 |
| A03 | POST /games | 作成 | F03・F13 |
| A04 | GET /games/active | 未完了取得 | F03・F13 |
| A05 | GET /games/{gameId} | 再開／保存後の共有用結果 | F04〜F06・F09・F12・F13 |
| A06 | POST /games/{gameId}/rolls | 個別／未抽選者一括 | F04〜F06・F13 |
| A07 | POST /games/{gameId}/rounds | 同点後の次ラウンド | F06・F13 |
| A08 | POST /games/{gameId}/payment-preview | 保存なし金額確認 | F07・F08 |
| A09 | POST /games/{gameId}/payment | 金額確定・集計 | F07〜F09・F11・F13 |
| A10 | GET /games | 負け額の履歴一覧（S06） | F10 |
| A11 | 廃止・欠番 | 全ラウンド閲覧APIは初期版から除外 | — |
| A12 | GET /statistics/losses | 累積負け額 | F11 |
| A13 | POST /games/{gameId}/cancel | 未完了中止 | F03・F13 |
| A14 | DELETE /games/{gameId} | 履歴削除・累積額減算 | F14・F13 |
| A15 | GET /games/{gameId}/history-detail | 抽選記録を含まない履歴詳細 | F10・F14 |

F01は全APIに適用。F05は描画専用API不要、F12は共有専用API不要。固定パス/games/activeをgameIdのルートより優先する。

## 4. API詳細

全APIに共通の401/403/429/503/500を適用する。各更新には共通のキー再利用409・欠落428を適用。以下は追加条件・業務エラー。

### A01 利用権限確認

本文・クエリなし。200 `{"authorized":true,"role":"administrator"}`。メールアドレス・Object IDを返さない。後続APIの認証を省略しない。

### A02 固定参加者取得

本文・クエリなし。200：

```json
{"participants":[{"participantId":"p001","displayName":"参加者A","sortOrder":1},{"participantId":"p002","displayName":"参加者B","sortOrder":2}]}
```

有効者のみ、sortOrder→participantId昇順。最大100件、ページングなし。2人未満でも200、画面は開始不可。初期データ未配置なら503。

### A03 ゲーム作成

Idempotency-Key必須。本文`{"participantIds":["p001","p002"]}`。選択順を保存。201 Game（revision=1、rolling、全員未抽選の第1ラウンド）とETag・`Location: .../games/{gameId}`。422は人数・重複・無効ID、409は未完了ゲームあり。同一成功再送は同じ201・Game。

### A04 未完了ゲーム取得

本文・クエリなし。200 `{"game":<Gameまたはnull>}`。存在時ETagあり、なしならETagなし。rolling/tie/awaitingPaymentだけ。DBポインター不整合は503で運用修復し、自動で新規を作らない。

### A05 詳細

本文・クエリなし。200 GameとETag。最新ラウンドのみを含み、S04再開・S05共有用結果に使う。S06はA15を使い、抽選記録を取得しない。完了・中止も参照可能。404は不存在／所有者外、410は削除済み。複数読取でゲームとラウンドを組み立てる際、最後にゲームの内部ETagを再確認し、同じrevisionの整合する組合せのみ返す。更新が続き安定しない場合503。

### A06 抽選

Idempotency-Key・If-Match必須。個別：

```json
{"roundNumber":1,"mode":"individual","participantId":"p001"}
```

未抽選者一括：

```json
{"roundNumber":1,"mode":"remaining"}
```

roundNumberは正整数。individualはparticipantId必須、remainingは付けない。rollingかつ現ラウンドだけ。200 更新後GameとETag。生成・全員完了時の判定・保存は一単位。409はラウンド相違、抽選済み、状態不正、422は参加者外ID等、412は競合。一括で確定済み値を変えない。

### A07 次ラウンド作成

Idempotency-Key・If-Match必須。本文`{"previousRoundNumber":1}`。tieかつ現番号一致のみ。201 更新後GameとETag、`Location: .../games/{gameId}`（A05で新しい現ラウンドを参照）。全員未抽選、rolling。ここでは抽選しない。409は状態・番号不正、412は競合。

### A08 金額プレビュー

本文`{"totalYen":13800}`。キー・If-Match不要、DB更新なし。awaitingPaymentのみ。200 `{"gameId":"<UUID>","revision":8,"payment":<registeredAtがnullのPayment>}`と参照ゲームETag。409は状態不正、422は総額不正。画面は保存にこのETagと同じ総額を使う。

### A09 金額確定

Idempotency-Key・If-Match必須。本文`{"totalYen":13800}`。awaitingPaymentからだけ更新、サーバー再計算。200 `{"game":<completedのGame>,"lossTotals":<A12と同形式>}`とゲームETag。完了ゲームへの別キー再登録は409（古いIf-Matchなら412）。同じ成功キーは再生、加算なし。422は総額不正。集計を含め一部失敗は全件ロールバック。

### A10 履歴一覧

limitは1〜50、既定20。初回cursor省略、続きはnextCursorをそのまま指定。200：

```json
{
  "items":[{
    "gameId":"7c56e8d0-1a58-4c45-8ef3-6505ad5c0a01",
    "createdAt":"2026-10-14T05:00:00Z",
    "loser":{"participantId":"p001","displayName":"参加者A"},
    "participantCount":2,
    "totalYen":7800,
    "lossYen":1800
  }],
  "nextCursor":null
}
```

削除されていないcompletedだけ、createdAt→gameId降順。敗者名は当時の参加者スナップショットから生成する。一覧に全参加者・出目・基準負担額を含めず、各自負担と金額登録日時はA15へ。負け額0円も一覧に含む。空items=[]、終端nextCursor=null。DB設計の境界カーソルを使用。途中の保存・削除について厳密な一覧スナップショットは保証しない。削除成功後はcursorを破棄して先頭から再取得する。

### A11 廃止（番号は再利用しない）

GET `/games/{gameId}/rounds`は初期版に実装しない（404）。画面変更で抽選記録の閲覧が不要になったため。POSTのA07は引き続き有効。抽選記録はDBに内部記録として保持し、S04/S05にはGame.currentRoundだけを返す。

### A12 累積額

本文・クエリなし。200：

```json
{
  "participants":[
    {"participantId":"p001","displayName":"参加者A","isActive":true,"totalLossYen":"1800"},
    {"participantId":"p002","displayName":"参加者B","isActive":true,"totalLossYen":"0"}
  ],
  "updatedAt":"2026-10-14T05:10:00Z"
}
```

現在の固定リスト全員（無効者も含む）をsortOrder→participantId順。最大100件、ページングなし。初期額0、updatedAt=null。削除済みゲームを集計から除外する。A09/A14直後はそのlossTotalsで画面を更新し、次のGETへ読取セッションを継承。

### A13 中止

Idempotency-Key・If-Match必須。本文`{}`。未完了3状態のみ。200 cancelledのGameとETag。ポインター解除、出目・確定済み敗者保持、payment=null、累積変更なし。409は完了・中止済み、412は競合。成功再送は元の結果。

### A14 履歴削除

DELETE `/games/{gameId}`。本文・クエリなし（本文ありは400）、Content-Type不要。Idempotency-Key・If-Match必須。S06のA15で取得したETagを使い、画面の確認操作後に実行する。

completedだけ削除可。論理削除を提案し、status=deleted・deletedAt設定・revision加算と、敗者の累積から保存済みpayment.lossYenを一回減算、成功操作記録作成を同じトランザクションで行う。負け額0円も削除可。未完了ゲームの中止はA13を使用する。別の未完了ゲームを指すcontrolは変更しない。

200応答の例（削除対象L=1,800、当該敗者の累積が1,800だった場合）：

```json
{
  "gameId":"7c56e8d0-1a58-4c45-8ef3-6505ad5c0a01",
  "status":"deleted",
  "revision":10,
  "deletedAt":"2026-10-14T05:20:00Z",
  "removedLoss":{"participantId":"p001","amountYen":1800},
  "lossTotals":{
    "participants":[
      {"participantId":"p001","displayName":"参加者A","isActive":true,"totalLossYen":"0"},
      {"participantId":"p002","displayName":"参加者B","isActive":true,"totalLossYen":"0"}
    ],
    "updatedAt":"2026-10-14T05:20:00Z"
  }
}
```

削除後のゲームETag・X-Read-Sessionを返す。同キー・元If-Matchの再送は同じ200で再生し、再減算なし。別キーの削除済み対象は410（ETag判定より先）、未完了・中止は409、版不一致412。不存在／所有者外404。累積がL未満なら503として全件未更新、運用修復を要求する。

削除成功時は端末の当該詳細・共有用結果・一覧キャッシュを破棄し、S06削除完了へ。A10を先頭から、A12を読取セッション付きで再取得。キャンセルはAPIを呼ばない。復元API・物理削除は初期版に含めない（方式はB10の提案）。

### A15 負け額の履歴詳細

GET `/games/{gameId}/history-detail`。本文・クエリなし。削除されていないcompletedのみ。200応答：

```json
{
  "gameId":"7c56e8d0-1a58-4c45-8ef3-6505ad5c0a01",
  "revision":9,
  "createdAt":"2026-10-14T05:00:00Z",
  "completedAt":"2026-10-14T05:10:00Z",
  "participants":[
    {"participantId":"p001","displayName":"参加者A","order":1},
    {"participantId":"p002","displayName":"参加者B","order":2}
  ],
  "loserParticipantId":"p001",
  "payment":{
    "totalYen":7800,"participantCount":2,"baseYen":3000,"lossYen":1800,
    "loserParticipantId":"p001","loserPaymentYen":4800,"collectFromOthersYen":3000,
    "shares":[{"participantId":"p001","amountYen":4800},{"participantId":"p002","amountYen":3000}],
    "calculationVersion":"thousand-remainder-v1","registeredAt":"2026-10-14T05:10:00Z"
  },
  "canDelete":true
}
```

ETagは同じゲームrevisionの更新用トークン。gameをPoint Readして当時名・会計を返し、roundは読まない。抽選・出目・同点回数を含めない。削除確認ではcreatedAt、敗者名、payment.lossYen/totalYenを使用。404は不存在／所有者外、410は削除済み、409は未完了・中止。

## 5. 呼出順序と未決定事項

1. ログイン→A01。ホームS02はA04/A12、参加者選択S03はA02、履歴S06を開いた時にA10。読取セッション取得後は継承。
2. A03→A06を個別反復またはremaining。
3. tieならA07→A06、awaitingPaymentならA08→確認→A09。
4. 保存結果をS05で表示。履歴S06はA10→A15→削除確認→A14→削除完了。戻る／キャンセルで削除要求は送信しない。
5. 通信断は同じ更新要求を再送、再読込はA04/A05、破棄はA13。

機能提案B01〜B10、ローカルDB方式は未決定。履歴削除自体は追加要件で、未決定なのは論理削除・復元非提供等の方式。OpenAPIの機械可読定義、具体的SDK型、API時間枠は実装時に本書から作成・検証する。
