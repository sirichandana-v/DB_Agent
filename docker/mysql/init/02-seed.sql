-- 50 synthetic rows per table (deterministic, ASCII only).
-- MySQL requires: INSERT ... WITH RECURSIVE ... SELECT (not WITH ... INSERT).
USE db_agent_test;

INSERT INTO departments (name, location, created_at)
WITH RECURSIVE seq AS (
    SELECT 1 AS n
    UNION ALL
    SELECT n + 1 FROM seq WHERE n < 50
)
SELECT
    CONCAT('Department ', LPAD(n, 3, '0')),
    CONCAT('Site-', ((n - 1) % 10) + 1),
    DATE_ADD('2020-01-01', INTERVAL n DAY)
FROM seq;

INSERT INTO employees (department_id, first_name, last_name, email, hire_date, salary)
WITH RECURSIVE seq AS (
    SELECT 1 AS n
    UNION ALL
    SELECT n + 1 FROM seq WHERE n < 50
)
SELECT
    n,
    CONCAT('First', n),
    CONCAT('Last', n),
    CONCAT('employee', LPAD(n, 3, '0'), '@example.test'),
    DATE_ADD('2021-06-01', INTERVAL n DAY),
    50000.00 + (n * 100)
FROM seq;

INSERT INTO projects (name, start_date, end_date, budget)
WITH RECURSIVE seq AS (
    SELECT 1 AS n
    UNION ALL
    SELECT n + 1 FROM seq WHERE n < 50
)
SELECT
    CONCAT('Project Alpha-', LPAD(n, 3, '0')),
    DATE_ADD('2022-01-15', INTERVAL n DAY),
    IF(n % 3 = 0, NULL, DATE_ADD('2023-06-01', INTERVAL n DAY)),
    100000.00 + (n * 2500)
FROM seq;

INSERT INTO assignments (employee_id, project_id, role_name, hours_allocated, assigned_date)
WITH RECURSIVE seq AS (
    SELECT 1 AS n
    UNION ALL
    SELECT n + 1 FROM seq WHERE n < 50
)
SELECT
    n,
    n,
    CASE (n % 4)
        WHEN 0 THEN 'Developer'
        WHEN 1 THEN 'Analyst'
        WHEN 2 THEN 'Lead'
        ELSE 'Contributor'
    END,
    80.00 + (n % 40),
    DATE_ADD('2023-01-01', INTERVAL n DAY)
FROM seq;
