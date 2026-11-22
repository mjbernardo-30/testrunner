-- Example DB verification step (Method = SQL). {{placeholders}} become SQL parameters.
SELECT OrderRefNo, OrderType, OrderStatus, FundCode, Amount
FROM   dbo.UTOrder WITH (NOLOCK)
WHERE  OrderRefNo = {{orderRefNo}}
