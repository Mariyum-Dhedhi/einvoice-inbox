# ERP posting contract

## First: a token (OAuth 2.0 client credentials)

Every data call carries `Authorization: Bearer <access token>`. The token comes from the token endpoint
(`Erp:TokenUrl`, default `<Erp:BaseUrl>oauth/token`; for D365 that is Microsoft Entra ID), as a form post:

```
POST oauth/token
Content-Type: application/x-www-form-urlencoded

grant_type=client_credentials&client_id=<Erp:ClientId>&client_secret=<Erp:ClientSecret>&scope=<Erp:Scope>
```

| Answer | Meaning |
|---|---|
| `200 {"access_token":"...","token_type":"Bearer","expires_in":3600}` | the token and how many seconds it is valid |
| `400`, `401` (`invalid_client`, `unsupported_grant_type`) | the ERP refuses who we are: every posting is final, "ERP refused the credentials." |
| `5xx`, no answer, an answer that is not this | no token right now: the posting is temporary and retried by the normal policy |

The application keeps one token and renews it **60 seconds before it expires**, not after a 401. A token that runs
out while a call is on its way would fail a call that was valid when it left, and every call that started in that
moment would fail together; renewing early means a valid call is never refused for its age. One renewal runs at a
time, so twenty calls starting together cause one token request.

If a data call still gets `401` (the ERP revoked the token, or its clock differs), the token is renewed once and the
call sent once more. A second `401` is final ("ERP refused the credentials."), never a loop. The secret and the
tokens appear only in the token request and the `Authorization` header: never in a log, an error, a stored reason or
the audit trail (`ErpCredentialLeakTests`).

## Then: the invoice

`POST data/VendorInvoices` with the header `Idempotency-Key: <invoice id>` and a body exactly like
`vendor-invoice.example.json` (camelCase, no field more, none missing; `purchaseOrder` and `purchaseOrderLine` may be
`null`).

| Answer | Meaning |
|---|---|
| `201 {"documentNumber":"VI-000123"}` | booked |
| `200` with the same number | this key was booked before (a retry after a lost reply) |
| `400` | the body is not this contract, or the key is missing |
| `401` | no valid token (renewed once, then final: see above) |
| `422` | the ERP refuses the invoice (for example a currency the company does not book): final |
| `408`, `5xx` | temporary: the application tries again later (`503` with `Retry-After`: then) |
| `429` | throttled: tries again after `Retry-After` (or the policy's wait) without using up a try |

Both sides test against this file: the application's connector (`HttpErpConnector`) and the mock ERP
(`tests/EInvoice.MockErp`). Change it only together with both.
