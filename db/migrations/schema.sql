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

