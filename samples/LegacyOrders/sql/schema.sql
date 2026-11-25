CREATE TABLE dbo.tblCustomer (
    CustomerId INT IDENTITY PRIMARY KEY,
    Name NVARCHAR(100) NOT NULL,
    Email NVARCHAR(200) NULL,
    IsVip BIT NOT NULL DEFAULT 0,
    CreditLimit DECIMAL(18,2) NOT NULL,
    LastOrderDate DATETIME2 NULL,
    CONSTRAINT CK_Customer_Credit CHECK (CreditLimit >= 0)
);
GO
CREATE TABLE dbo.tblOrder (
    OrderId INT IDENTITY PRIMARY KEY,
    CustomerId INT NOT NULL REFERENCES dbo.tblCustomer(CustomerId),
    CreatedOn DATETIME2 NOT NULL,
    Status INT NOT NULL,
    Subtotal DECIMAL(18,2) NOT NULL,
    Discount DECIMAL(18,2) NOT NULL,
    Total DECIMAL(18,2) NOT NULL,
    IsDeleted BIT NOT NULL DEFAULT 0
);
GO
CREATE PROCEDURE dbo.usp_GetAvailableCredit @CustomerId INT AS
BEGIN
    IF NOT EXISTS (SELECT 1 FROM dbo.tblCustomer WHERE CustomerId = @CustomerId)
        RAISERROR('Unknown customer', 16, 1);
    SELECT c.CreditLimit - ISNULL(SUM(o.Total), 0)
    FROM dbo.tblCustomer c LEFT JOIN dbo.tblOrder o ON o.CustomerId = c.CustomerId AND o.Status IN (1, 2)
    WHERE c.CustomerId = @CustomerId
    GROUP BY c.CreditLimit;
END
GO
