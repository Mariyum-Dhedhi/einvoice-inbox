IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112230731_InitialSchema'
)
BEGIN
    CREATE TABLE [dbo].[Vendor] (
        [Id] uniqueidentifier NOT NULL,
        [Number] nvarchar(20) NOT NULL,
        [Name] nvarchar(200) NOT NULL,
        [VatId] varchar(20) NULL,
        [IsActive] bit NOT NULL,
        [RowId] bigint NOT NULL IDENTITY,
        CONSTRAINT [PK_Vendor] PRIMARY KEY NONCLUSTERED ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112230731_InitialSchema'
)
BEGIN
    CREATE TABLE [dbo].[Invoice] (
        [Id] uniqueidentifier NOT NULL,
        [SourceFormat] int NOT NULL,
        [SubmittedBy] nvarchar(256) NOT NULL,
        [Status] int NOT NULL,
        [InvoiceNumber] nvarchar(50) NULL,
        [VendorId] uniqueidentifier NULL,
        [IssueDate] date NULL,
        [PurchaseOrderNumber] nvarchar(20) NULL,
        [RowId] bigint NOT NULL IDENTITY,
        [GrossTotal_Amount] decimal(18,2) NULL,
        [GrossTotal_Currency] char(3) NULL,
        [NetTotal_Amount] decimal(18,2) NULL,
        [NetTotal_Currency] char(3) NULL,
        [TaxTotal_Amount] decimal(18,2) NULL,
        [TaxTotal_Currency] char(3) NULL,
        CONSTRAINT [PK_Invoice] PRIMARY KEY NONCLUSTERED ([Id]),
        CONSTRAINT [CK_Invoice_SourceFormat] CHECK ([SourceFormat] IN (1, 2, 3)),
        CONSTRAINT [CK_Invoice_Status] CHECK ([Status] IN (1, 2, 3, 4, 5, 6, 7, 8, 9)),
        CONSTRAINT [FK_Invoice_Vendor_VendorId] FOREIGN KEY ([VendorId]) REFERENCES [dbo].[Vendor] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112230731_InitialSchema'
)
BEGIN
    CREATE TABLE [dbo].[PurchaseOrder] (
        [Id] uniqueidentifier NOT NULL,
        [Number] nvarchar(20) NOT NULL,
        [VendorId] uniqueidentifier NOT NULL,
        [OrderDate] date NOT NULL,
        [Currency] char(3) NOT NULL,
        [RowId] bigint NOT NULL IDENTITY,
        CONSTRAINT [PK_PurchaseOrder] PRIMARY KEY NONCLUSTERED ([Id]),
        CONSTRAINT [FK_PurchaseOrder_Vendor_VendorId] FOREIGN KEY ([VendorId]) REFERENCES [dbo].[Vendor] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112230731_InitialSchema'
)
BEGIN
    CREATE TABLE [dbo].[InvoiceLine] (
        [LineNumber] int NOT NULL,
        [InvoiceId] uniqueidentifier NOT NULL,
        [Description] nvarchar(500) NOT NULL,
        [Quantity] decimal(18,4) NOT NULL,
        [UnitPrice] decimal(18,6) NOT NULL,
        [TaxRatePercent] decimal(5,2) NOT NULL,
        [PurchaseOrderLineNumber] int NULL,
        [RowId] bigint NOT NULL IDENTITY,
        [NetAmount_Amount] decimal(18,2) NOT NULL,
        [NetAmount_Currency] char(3) NOT NULL,
        CONSTRAINT [PK_InvoiceLine] PRIMARY KEY NONCLUSTERED ([InvoiceId], [LineNumber]),
        CONSTRAINT [FK_InvoiceLine_Invoice_InvoiceId] FOREIGN KEY ([InvoiceId]) REFERENCES [dbo].[Invoice] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112230731_InitialSchema'
)
BEGIN
    CREATE TABLE [dbo].[InvoiceStatusChange] (
        [Sequence] int NOT NULL,
        [InvoiceId] uniqueidentifier NOT NULL,
        [FromStatus] int NULL,
        [ToStatus] int NOT NULL,
        [Actor] nvarchar(256) NOT NULL,
        [At] datetimeoffset NOT NULL,
        [Reason] nvarchar(200) NULL,
        [RowId] bigint NOT NULL IDENTITY,
        CONSTRAINT [PK_InvoiceStatusChange] PRIMARY KEY NONCLUSTERED ([InvoiceId], [Sequence]),
        CONSTRAINT [CK_InvoiceStatusChange_FromStatus] CHECK ([FromStatus] IN (1, 2, 3, 4, 5, 6, 7, 8, 9)),
        CONSTRAINT [CK_InvoiceStatusChange_ToStatus] CHECK ([ToStatus] IN (1, 2, 3, 4, 5, 6, 7, 8, 9)),
        CONSTRAINT [FK_InvoiceStatusChange_Invoice_InvoiceId] FOREIGN KEY ([InvoiceId]) REFERENCES [dbo].[Invoice] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112230731_InitialSchema'
)
BEGIN
    CREATE TABLE [dbo].[GoodsReceipt] (
        [Id] uniqueidentifier NOT NULL,
        [Number] nvarchar(20) NOT NULL,
        [PurchaseOrderId] uniqueidentifier NOT NULL,
        [ReceivedDate] date NOT NULL,
        [RowId] bigint NOT NULL IDENTITY,
        CONSTRAINT [PK_GoodsReceipt] PRIMARY KEY NONCLUSTERED ([Id]),
        CONSTRAINT [FK_GoodsReceipt_PurchaseOrder_PurchaseOrderId] FOREIGN KEY ([PurchaseOrderId]) REFERENCES [dbo].[PurchaseOrder] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112230731_InitialSchema'
)
BEGIN
    CREATE TABLE [dbo].[PurchaseOrderLine] (
        [LineNumber] int NOT NULL,
        [PurchaseOrderId] uniqueidentifier NOT NULL,
        [ItemNumber] nvarchar(50) NOT NULL,
        [Description] nvarchar(500) NOT NULL,
        [Quantity] decimal(18,4) NOT NULL,
        [UnitPrice] decimal(18,6) NOT NULL,
        [TaxRatePercent] decimal(5,2) NOT NULL,
        [RowId] bigint NOT NULL IDENTITY,
        CONSTRAINT [PK_PurchaseOrderLine] PRIMARY KEY NONCLUSTERED ([PurchaseOrderId], [LineNumber]),
        CONSTRAINT [FK_PurchaseOrderLine_PurchaseOrder_PurchaseOrderId] FOREIGN KEY ([PurchaseOrderId]) REFERENCES [dbo].[PurchaseOrder] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112230731_InitialSchema'
)
BEGIN
    CREATE TABLE [dbo].[GoodsReceiptLine] (
        [PurchaseOrderLineNumber] int NOT NULL,
        [GoodsReceiptId] uniqueidentifier NOT NULL,
        [QuantityReceived] decimal(18,4) NOT NULL,
        [RowId] bigint NOT NULL IDENTITY,
        CONSTRAINT [PK_GoodsReceiptLine] PRIMARY KEY NONCLUSTERED ([GoodsReceiptId], [PurchaseOrderLineNumber]),
        CONSTRAINT [FK_GoodsReceiptLine_GoodsReceipt_GoodsReceiptId] FOREIGN KEY ([GoodsReceiptId]) REFERENCES [dbo].[GoodsReceipt] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112230731_InitialSchema'
)
BEGIN
    CREATE CLUSTERED INDEX [CX_GoodsReceipt_RowId] ON [dbo].[GoodsReceipt] ([RowId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112230731_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_GoodsReceipt_PurchaseOrderId] ON [dbo].[GoodsReceipt] ([PurchaseOrderId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112230731_InitialSchema'
)
BEGIN
    CREATE UNIQUE INDEX [UX_GoodsReceipt_Number] ON [dbo].[GoodsReceipt] ([Number]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112230731_InitialSchema'
)
BEGIN
    CREATE CLUSTERED INDEX [CX_GoodsReceiptLine_RowId] ON [dbo].[GoodsReceiptLine] ([RowId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112230731_InitialSchema'
)
BEGIN
    CREATE CLUSTERED INDEX [CX_Invoice_RowId] ON [dbo].[Invoice] ([RowId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112230731_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_Invoice_Status] ON [dbo].[Invoice] ([Status]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112230731_InitialSchema'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_Invoice_Vendor_InvoiceNumber] ON [dbo].[Invoice] ([VendorId], [InvoiceNumber]) WHERE [VendorId] IS NOT NULL AND [InvoiceNumber] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112230731_InitialSchema'
)
BEGIN
    CREATE CLUSTERED INDEX [CX_InvoiceLine_RowId] ON [dbo].[InvoiceLine] ([RowId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112230731_InitialSchema'
)
BEGIN
    CREATE CLUSTERED INDEX [CX_InvoiceStatusChange_RowId] ON [dbo].[InvoiceStatusChange] ([RowId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112230731_InitialSchema'
)
BEGIN
    CREATE CLUSTERED INDEX [CX_PurchaseOrder_RowId] ON [dbo].[PurchaseOrder] ([RowId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112230731_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_PurchaseOrder_VendorId] ON [dbo].[PurchaseOrder] ([VendorId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112230731_InitialSchema'
)
BEGIN
    CREATE UNIQUE INDEX [UX_PurchaseOrder_Number] ON [dbo].[PurchaseOrder] ([Number]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112230731_InitialSchema'
)
BEGIN
    CREATE CLUSTERED INDEX [CX_PurchaseOrderLine_RowId] ON [dbo].[PurchaseOrderLine] ([RowId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112230731_InitialSchema'
)
BEGIN
    CREATE CLUSTERED INDEX [CX_Vendor_RowId] ON [dbo].[Vendor] ([RowId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112230731_InitialSchema'
)
BEGIN
    CREATE UNIQUE INDEX [UX_Vendor_Number] ON [dbo].[Vendor] ([Number]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112230731_InitialSchema'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261112230731_InitialSchema', N'10.0.14');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261113185401_AddVendorBankAccountAndVatIdIndex'
)
BEGIN
    CREATE TABLE [dbo].[VendorBankAccount] (
        [Id] uniqueidentifier NOT NULL,
        [VendorId] uniqueidentifier NOT NULL,
        [Iban] varchar(34) NOT NULL,
        [RowId] bigint NOT NULL IDENTITY,
        CONSTRAINT [PK_VendorBankAccount] PRIMARY KEY NONCLUSTERED ([Id]),
        CONSTRAINT [FK_VendorBankAccount_Vendor_VendorId] FOREIGN KEY ([VendorId]) REFERENCES [dbo].[Vendor] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261113185401_AddVendorBankAccountAndVatIdIndex'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_Vendor_VatId] ON [dbo].[Vendor] ([VatId]) WHERE [VatId] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261113185401_AddVendorBankAccountAndVatIdIndex'
)
BEGIN
    CREATE CLUSTERED INDEX [CX_VendorBankAccount_RowId] ON [dbo].[VendorBankAccount] ([RowId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261113185401_AddVendorBankAccountAndVatIdIndex'
)
BEGIN
    CREATE UNIQUE INDEX [UX_VendorBankAccount_Vendor_Iban] ON [dbo].[VendorBankAccount] ([VendorId], [Iban]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261113185401_AddVendorBankAccountAndVatIdIndex'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261113185401_AddVendorBankAccountAndVatIdIndex', N'10.0.14');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261113192242_AddMatchResult'
)
BEGIN
    CREATE TABLE [dbo].[MatchResult] (
        [InvoiceId] uniqueidentifier NOT NULL,
        [InvoiceLineNumber] int NOT NULL,
        [PurchaseOrderLineNumber] int NULL,
        [Outcome] int NOT NULL,
        [InvoiceUnitPrice] decimal(18,6) NOT NULL,
        [OrderUnitPrice] decimal(18,6) NULL,
        [PriceVariancePercent] decimal(19,6) NULL,
        [PriceVarianceAmount] decimal(18,2) NULL,
        [Currency] char(3) NOT NULL,
        [QuantityInvoiced] decimal(18,4) NOT NULL,
        [QuantityAvailable] decimal(18,4) NULL,
        [QuantityVariance] decimal(18,4) NULL,
        [PriceTolerancePercent] decimal(5,2) NOT NULL,
        [MatchedAt] datetimeoffset NOT NULL,
        [RowId] bigint NOT NULL IDENTITY,
        CONSTRAINT [PK_MatchResult] PRIMARY KEY NONCLUSTERED ([InvoiceId], [InvoiceLineNumber]),
        CONSTRAINT [CK_MatchResult_Outcome] CHECK ([Outcome] IN (1, 2, 3, 4, 5)),
        CONSTRAINT [FK_MatchResult_Invoice_InvoiceId] FOREIGN KEY ([InvoiceId]) REFERENCES [dbo].[Invoice] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261113192242_AddMatchResult'
)
BEGIN
    CREATE CLUSTERED INDEX [CX_MatchResult_RowId] ON [dbo].[MatchResult] ([RowId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261113192242_AddMatchResult'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261113192242_AddMatchResult', N'10.0.14');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261114002726_AddInvoiceDocumentAndFindings'
)
BEGIN
    CREATE TABLE [dbo].[InvoiceDocument] (
        [InvoiceId] uniqueidentifier NOT NULL,
        [FileName] nvarchar(255) NOT NULL,
        [ContentType] varchar(100) NOT NULL,
        [Content] varbinary(max) NOT NULL,
        [Sha256] binary(32) NOT NULL,
        [SizeBytes] int NOT NULL,
        [ReceivedAt] datetimeoffset NOT NULL,
        [RowId] bigint NOT NULL IDENTITY,
        CONSTRAINT [PK_InvoiceDocument] PRIMARY KEY NONCLUSTERED ([InvoiceId]),
        CONSTRAINT [FK_InvoiceDocument_Invoice_InvoiceId] FOREIGN KEY ([InvoiceId]) REFERENCES [dbo].[Invoice] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261114002726_AddInvoiceDocumentAndFindings'
)
BEGIN
    CREATE TABLE [dbo].[InvoiceFinding] (
        [InvoiceId] uniqueidentifier NOT NULL,
        [Sequence] int NOT NULL,
        [RuleCode] varchar(40) NOT NULL,
        [Severity] int NOT NULL,
        [Message] nvarchar(500) NOT NULL,
        [LinePosition] int NULL,
        [RowId] bigint NOT NULL IDENTITY,
        CONSTRAINT [PK_InvoiceFinding] PRIMARY KEY NONCLUSTERED ([InvoiceId], [Sequence]),
        CONSTRAINT [CK_InvoiceFinding_Severity] CHECK ([Severity] IN (1, 2)),
        CONSTRAINT [FK_InvoiceFinding_Invoice_InvoiceId] FOREIGN KEY ([InvoiceId]) REFERENCES [dbo].[Invoice] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261114002726_AddInvoiceDocumentAndFindings'
)
BEGIN
    CREATE CLUSTERED INDEX [CX_InvoiceDocument_RowId] ON [dbo].[InvoiceDocument] ([RowId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261114002726_AddInvoiceDocumentAndFindings'
)
BEGIN
    CREATE UNIQUE INDEX [UX_InvoiceDocument_Sha256] ON [dbo].[InvoiceDocument] ([Sha256]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261114002726_AddInvoiceDocumentAndFindings'
)
BEGIN
    CREATE CLUSTERED INDEX [CX_InvoiceFinding_RowId] ON [dbo].[InvoiceFinding] ([RowId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261114002726_AddInvoiceDocumentAndFindings'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261114002726_AddInvoiceDocumentAndFindings', N'10.0.14');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115151424_AddSignIn'
)
BEGIN
    CREATE SEQUENCE [dbo].[AppRoleClaimId] START WITH 1 INCREMENT BY 1 NO CYCLE;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115151424_AddSignIn'
)
BEGIN
    CREATE SEQUENCE [dbo].[AppUserClaimId] START WITH 1 INCREMENT BY 1 NO CYCLE;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115151424_AddSignIn'
)
BEGIN
    CREATE TABLE [dbo].[AppRole] (
        [Id] uniqueidentifier NOT NULL,
        [RowId] bigint NOT NULL IDENTITY,
        [Name] nvarchar(256) NULL,
        [NormalizedName] nvarchar(256) NULL,
        [ConcurrencyStamp] varchar(64) NULL,
        CONSTRAINT [PK_AppRole] PRIMARY KEY NONCLUSTERED ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115151424_AddSignIn'
)
BEGIN
    CREATE TABLE [dbo].[AppUser] (
        [Id] uniqueidentifier NOT NULL,
        [RowId] bigint NOT NULL IDENTITY,
        [UserName] nvarchar(256) NULL,
        [NormalizedUserName] nvarchar(256) NULL,
        [Email] nvarchar(256) NULL,
        [NormalizedEmail] nvarchar(256) NULL,
        [EmailConfirmed] bit NOT NULL,
        [PasswordHash] varchar(256) NULL,
        [SecurityStamp] varchar(64) NULL,
        [ConcurrencyStamp] varchar(64) NULL,
        [PhoneNumber] nvarchar(50) NULL,
        [PhoneNumberConfirmed] bit NOT NULL,
        [TwoFactorEnabled] bit NOT NULL,
        [LockoutEnd] datetimeoffset NULL,
        [LockoutEnabled] bit NOT NULL,
        [AccessFailedCount] int NOT NULL,
        CONSTRAINT [PK_AppUser] PRIMARY KEY NONCLUSTERED ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115151424_AddSignIn'
)
BEGIN
    CREATE TABLE [dbo].[AppRoleClaim] (
        [Id] int NOT NULL DEFAULT (NEXT VALUE FOR [dbo].[AppRoleClaimId]),
        [RoleId] uniqueidentifier NOT NULL,
        [ClaimType] nvarchar(256) NULL,
        [ClaimValue] nvarchar(512) NULL,
        [RowId] bigint NOT NULL IDENTITY,
        CONSTRAINT [PK_AppRoleClaim] PRIMARY KEY NONCLUSTERED ([Id]),
        CONSTRAINT [FK_AppRoleClaim_AppRole_RoleId] FOREIGN KEY ([RoleId]) REFERENCES [dbo].[AppRole] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115151424_AddSignIn'
)
BEGIN
    CREATE TABLE [dbo].[AppUserClaim] (
        [Id] int NOT NULL DEFAULT (NEXT VALUE FOR [dbo].[AppUserClaimId]),
        [UserId] uniqueidentifier NOT NULL,
        [ClaimType] nvarchar(256) NULL,
        [ClaimValue] nvarchar(512) NULL,
        [RowId] bigint NOT NULL IDENTITY,
        CONSTRAINT [PK_AppUserClaim] PRIMARY KEY NONCLUSTERED ([Id]),
        CONSTRAINT [FK_AppUserClaim_AppUser_UserId] FOREIGN KEY ([UserId]) REFERENCES [dbo].[AppUser] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115151424_AddSignIn'
)
BEGIN
    CREATE TABLE [dbo].[AppUserLogin] (
        [LoginProvider] nvarchar(128) NOT NULL,
        [ProviderKey] nvarchar(128) NOT NULL,
        [ProviderDisplayName] nvarchar(256) NULL,
        [UserId] uniqueidentifier NOT NULL,
        [RowId] bigint NOT NULL IDENTITY,
        CONSTRAINT [PK_AppUserLogin] PRIMARY KEY NONCLUSTERED ([LoginProvider], [ProviderKey]),
        CONSTRAINT [FK_AppUserLogin_AppUser_UserId] FOREIGN KEY ([UserId]) REFERENCES [dbo].[AppUser] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115151424_AddSignIn'
)
BEGIN
    CREATE TABLE [dbo].[AppUserRole] (
        [UserId] uniqueidentifier NOT NULL,
        [RoleId] uniqueidentifier NOT NULL,
        [RowId] bigint NOT NULL IDENTITY,
        CONSTRAINT [PK_AppUserRole] PRIMARY KEY NONCLUSTERED ([UserId], [RoleId]),
        CONSTRAINT [FK_AppUserRole_AppRole_RoleId] FOREIGN KEY ([RoleId]) REFERENCES [dbo].[AppRole] ([Id]) ON DELETE CASCADE,
        CONSTRAINT [FK_AppUserRole_AppUser_UserId] FOREIGN KEY ([UserId]) REFERENCES [dbo].[AppUser] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115151424_AddSignIn'
)
BEGIN
    CREATE TABLE [dbo].[AppUserToken] (
        [UserId] uniqueidentifier NOT NULL,
        [LoginProvider] nvarchar(128) NOT NULL,
        [Name] nvarchar(128) NOT NULL,
        [Value] nvarchar(1024) NULL,
        [RowId] bigint NOT NULL IDENTITY,
        CONSTRAINT [PK_AppUserToken] PRIMARY KEY NONCLUSTERED ([UserId], [LoginProvider], [Name]),
        CONSTRAINT [FK_AppUserToken_AppUser_UserId] FOREIGN KEY ([UserId]) REFERENCES [dbo].[AppUser] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115151424_AddSignIn'
)
BEGIN
    IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'Id', N'ConcurrencyStamp', N'Name', N'NormalizedName') AND [object_id] = OBJECT_ID(N'[dbo].[AppRole]'))
        SET IDENTITY_INSERT [dbo].[AppRole] ON;
    EXEC(N'INSERT INTO [dbo].[AppRole] ([Id], [ConcurrencyStamp], [Name], [NormalizedName])
    VALUES (''6f1c2a35-2f61-4d4e-9a0b-1b7a3f0c1001'', ''6f1c2a352f614d4e9a0b1b7a3f0c1001'', N''Clerk'', N''CLERK''),
    (''6f1c2a35-2f61-4d4e-9a0b-1b7a3f0c1002'', ''6f1c2a352f614d4e9a0b1b7a3f0c1002'', N''Approver'', N''APPROVER''),
    (''6f1c2a35-2f61-4d4e-9a0b-1b7a3f0c1003'', ''6f1c2a352f614d4e9a0b1b7a3f0c1003'', N''Auditor'', N''AUDITOR''),
    (''6f1c2a35-2f61-4d4e-9a0b-1b7a3f0c1004'', ''6f1c2a352f614d4e9a0b1b7a3f0c1004'', N''Admin'', N''ADMIN'')');
    IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'Id', N'ConcurrencyStamp', N'Name', N'NormalizedName') AND [object_id] = OBJECT_ID(N'[dbo].[AppRole]'))
        SET IDENTITY_INSERT [dbo].[AppRole] OFF;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115151424_AddSignIn'
)
BEGIN
    CREATE CLUSTERED INDEX [CX_AppRole_RowId] ON [dbo].[AppRole] ([RowId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115151424_AddSignIn'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [RoleNameIndex] ON [dbo].[AppRole] ([NormalizedName]) WHERE [NormalizedName] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115151424_AddSignIn'
)
BEGIN
    CREATE CLUSTERED INDEX [CX_AppRoleClaim_RowId] ON [dbo].[AppRoleClaim] ([RowId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115151424_AddSignIn'
)
BEGIN
    CREATE INDEX [IX_AppRoleClaim_RoleId] ON [dbo].[AppRoleClaim] ([RoleId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115151424_AddSignIn'
)
BEGIN
    CREATE CLUSTERED INDEX [CX_AppUser_RowId] ON [dbo].[AppUser] ([RowId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115151424_AddSignIn'
)
BEGIN
    CREATE INDEX [EmailIndex] ON [dbo].[AppUser] ([NormalizedEmail]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115151424_AddSignIn'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UserNameIndex] ON [dbo].[AppUser] ([NormalizedUserName]) WHERE [NormalizedUserName] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115151424_AddSignIn'
)
BEGIN
    CREATE CLUSTERED INDEX [CX_AppUserClaim_RowId] ON [dbo].[AppUserClaim] ([RowId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115151424_AddSignIn'
)
BEGIN
    CREATE INDEX [IX_AppUserClaim_UserId] ON [dbo].[AppUserClaim] ([UserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115151424_AddSignIn'
)
BEGIN
    CREATE CLUSTERED INDEX [CX_AppUserLogin_RowId] ON [dbo].[AppUserLogin] ([RowId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115151424_AddSignIn'
)
BEGIN
    CREATE INDEX [IX_AppUserLogin_UserId] ON [dbo].[AppUserLogin] ([UserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115151424_AddSignIn'
)
BEGIN
    CREATE CLUSTERED INDEX [CX_AppUserRole_RowId] ON [dbo].[AppUserRole] ([RowId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115151424_AddSignIn'
)
BEGIN
    CREATE INDEX [IX_AppUserRole_RoleId] ON [dbo].[AppUserRole] ([RoleId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115151424_AddSignIn'
)
BEGIN
    CREATE CLUSTERED INDEX [CX_AppUserToken_RowId] ON [dbo].[AppUserToken] ([RowId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115151424_AddSignIn'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261115151424_AddSignIn', N'10.0.14');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115204127_AddInvoiceVersion'
)
BEGIN
    ALTER TABLE [dbo].[Invoice] ADD [Version] rowversion NOT NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115204127_AddInvoiceVersion'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261115204127_AddInvoiceVersion', N'10.0.14');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115213308_AddAuditEvent'
)
BEGIN
    CREATE TABLE [dbo].[AuditEvent] (
        [Id] uniqueidentifier NOT NULL,
        [Sequence] bigint NOT NULL,
        [At] datetimeoffset NOT NULL,
        [Actor] nvarchar(256) NOT NULL,
        [Action] varchar(50) NOT NULL,
        [Subject] nvarchar(300) NOT NULL,
        [Details] nvarchar(2000) NOT NULL,
        [PreviousHash] binary(32) NOT NULL,
        [Hash] binary(32) NOT NULL,
        [RowId] bigint NOT NULL IDENTITY,
        CONSTRAINT [PK_AuditEvent] PRIMARY KEY NONCLUSTERED ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115213308_AddAuditEvent'
)
BEGIN
    CREATE CLUSTERED INDEX [CX_AuditEvent_RowId] ON [dbo].[AuditEvent] ([RowId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115213308_AddAuditEvent'
)
BEGIN
    CREATE INDEX [IX_AuditEvent_Subject] ON [dbo].[AuditEvent] ([Subject]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115213308_AddAuditEvent'
)
BEGIN
    CREATE UNIQUE INDEX [UX_AuditEvent_Sequence] ON [dbo].[AuditEvent] ([Sequence]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115213308_AddAuditEvent'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261115213308_AddAuditEvent', N'10.0.14');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261116050049_AddBankAccountTwoPersonRule'
)
BEGIN
    ALTER TABLE [dbo].[VendorBankAccount] ADD [ConfirmedAt] datetimeoffset NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261116050049_AddBankAccountTwoPersonRule'
)
BEGIN
    ALTER TABLE [dbo].[VendorBankAccount] ADD [ConfirmedBy] nvarchar(256) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261116050049_AddBankAccountTwoPersonRule'
)
BEGIN
    ALTER TABLE [dbo].[VendorBankAccount] ADD [ProposedAt] datetimeoffset NOT NULL DEFAULT '0001-01-01T00:00:00.0000000+00:00';
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261116050049_AddBankAccountTwoPersonRule'
)
BEGIN
    ALTER TABLE [dbo].[VendorBankAccount] ADD [ProposedBy] nvarchar(256) NOT NULL DEFAULT N'';
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261116050049_AddBankAccountTwoPersonRule'
)
BEGIN
    ALTER TABLE [dbo].[VendorBankAccount] ADD [Status] int NOT NULL DEFAULT 1;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261116050049_AddBankAccountTwoPersonRule'
)
BEGIN
    ALTER TABLE [dbo].[VendorBankAccount] ADD [ValidFrom] datetime2 NOT NULL DEFAULT '0001-01-01T00:00:00.0000000';
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261116050049_AddBankAccountTwoPersonRule'
)
BEGIN
    ALTER TABLE [dbo].[VendorBankAccount] ADD [ValidTo] datetime2 NOT NULL DEFAULT '9999-12-31T23:59:59.9999999';
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261116050049_AddBankAccountTwoPersonRule'
)
BEGIN
    ALTER TABLE [dbo].[VendorBankAccount] ADD [Version] rowversion NOT NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261116050049_AddBankAccountTwoPersonRule'
)
BEGIN
    EXEC(N'ALTER TABLE [dbo].[VendorBankAccount] ADD CONSTRAINT [CK_VendorBankAccount_Status] CHECK ([Status] IN (1, 2))');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261116050049_AddBankAccountTwoPersonRule'
)
BEGIN
    EXEC(N'ALTER TABLE [dbo].[VendorBankAccount] ADD PERIOD FOR SYSTEM_TIME ([ValidFrom], [ValidTo])')
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261116050049_AddBankAccountTwoPersonRule'
)
BEGIN
    ALTER TABLE [dbo].[VendorBankAccount] ALTER COLUMN [ValidFrom] ADD HIDDEN
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261116050049_AddBankAccountTwoPersonRule'
)
BEGIN
    ALTER TABLE [dbo].[VendorBankAccount] ALTER COLUMN [ValidTo] ADD HIDDEN
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261116050049_AddBankAccountTwoPersonRule'
)
BEGIN
    ALTER TABLE [dbo].[VendorBankAccount] SET (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [dbo].[VendorBankAccountHistory]))

END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261116050049_AddBankAccountTwoPersonRule'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261116050049_AddBankAccountTwoPersonRule', N'10.0.14');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261116083610_AddPostingJob'
)
BEGIN
    CREATE TABLE [dbo].[PostingJob] (
        [Id] uniqueidentifier NOT NULL,
        [InvoiceId] uniqueidentifier NOT NULL,
        [Status] int NOT NULL,
        [Attempts] int NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [NextAttemptAt] datetimeoffset NOT NULL,
        [LeaseUntil] datetimeoffset NULL,
        [ProcessedAt] datetimeoffset NULL,
        [LastError] nvarchar(200) NULL,
        [ErpDocumentNumber] nvarchar(50) NULL,
        [RowId] bigint NOT NULL IDENTITY,
        CONSTRAINT [PK_PostingJob] PRIMARY KEY NONCLUSTERED ([Id]),
        CONSTRAINT [CK_PostingJob_Status] CHECK ([Status] IN (1, 2, 3)),
        CONSTRAINT [FK_PostingJob_Invoice_InvoiceId] FOREIGN KEY ([InvoiceId]) REFERENCES [dbo].[Invoice] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261116083610_AddPostingJob'
)
BEGIN
    CREATE CLUSTERED INDEX [CX_PostingJob_RowId] ON [dbo].[PostingJob] ([RowId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261116083610_AddPostingJob'
)
BEGIN
    CREATE INDEX [IX_PostingJob_Due] ON [dbo].[PostingJob] ([Status], [NextAttemptAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261116083610_AddPostingJob'
)
BEGIN
    CREATE INDEX [IX_PostingJob_InvoiceId] ON [dbo].[PostingJob] ([InvoiceId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261116083610_AddPostingJob'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261116083610_AddPostingJob', N'10.0.14');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261116105612_AddInvoicePurchaseOrderIndex'
)
BEGIN
    CREATE INDEX [IX_Invoice_PurchaseOrderNumber] ON [dbo].[Invoice] ([PurchaseOrderNumber]) INCLUDE ([Id], [Status]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261116105612_AddInvoicePurchaseOrderIndex'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261116105612_AddInvoicePurchaseOrderIndex', N'10.0.14');
END;

COMMIT;
GO

