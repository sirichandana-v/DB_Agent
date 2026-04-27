-- Large synthetic data (ASCII only). Requires MySQL 8+ for CTEs.
-- Raise recursion cap for 200+ row series in one CTE.
SET SESSION cte_max_recursion_depth = 1000000;

USE db_agent_test;

-- 50 departments
INSERT INTO departments (name, location, cost_center, headcount_target, created_at)
WITH RECURSIVE seq AS (SELECT 1 AS n UNION ALL SELECT n + 1 FROM seq WHERE n < 50)
SELECT
    CONCAT('Department ', LPAD(n, 3, '0')),
    CONCAT('Site-', ((n - 1) % 10) + 1),
    CONCAT('CC-', LPAD((n - 1) % 20 + 1, 2, '0')),
    5 + (n % 12),
    DATE_ADD('2020-01-01', INTERVAL n DAY)
FROM seq;

-- 200 employees
INSERT INTO employees (department_id, first_name, last_name, email, phone_ext, job_title, hire_date, emp_status, salary)
WITH RECURSIVE seq AS (SELECT 1 AS n UNION ALL SELECT n + 1 FROM seq WHERE n < 200)
SELECT
    ((n - 1) % 50) + 1,
    CONCAT('First', n),
    CONCAT('Last', n),
    CONCAT('employee', LPAD(n, 3, '0'), '@example.test'),
    LPAD((n % 100), 3, '0'),
    ELT(1 + (n % 6), 'Analyst', 'Engineer', 'Manager', 'Developer', 'Consultant', 'Admin'),
    DATE_ADD('2021-06-01', INTERVAL n DAY),
    IF(n % 40 = 0, 'leave', 'active'),
    48000.00 + (n * 125)
FROM seq;

-- 100 projects
INSERT INTO projects (name, start_date, end_date, budget, priority, risk_level)
WITH RECURSIVE seq AS (SELECT 1 AS n UNION ALL SELECT n + 1 FROM seq WHERE n < 100)
SELECT
    CONCAT('Project Alpha-', LPAD(n, 3, '0')),
    DATE_ADD('2022-01-15', INTERVAL n DAY),
    IF(n % 3 = 0, NULL, DATE_ADD('2023-06-01', INTERVAL n DAY)),
    100000.00 + (n * 2500),
    1 + (n % 5),
    ELT(1 + (n % 4), 'low', 'medium', 'high', 'critical')
FROM seq;

-- 150 customers
INSERT INTO customers (code, name, country, region, tier, credit_limit, created_at, is_active)
WITH RECURSIVE seq AS (SELECT 1 AS n UNION ALL SELECT n + 1 FROM seq WHERE n < 150)
SELECT
    CONCAT('CUST-', LPAD(n, 5, '0')),
    CONCAT('Customer ', n, ' Co'),
    ELT(1 + (n % 5), 'US', 'CA', 'GB', 'DE', 'FR'),
    CONCAT('Region-', ((n - 1) % 7) + 1),
    ELT(1 + (n % 3), 'standard', 'gold', 'platinum'),
    10000.00 + (n * 500.00),
    DATE_ADD('2019-01-01 10:00:00', INTERVAL n HOUR),
    IF(n % 20 = 0, 0, 1)
FROM seq;

-- 200 products
INSERT INTO products (sku, name, category, subcategory, list_price, cost, stock_on_hand, reorder_level, is_discontinued)
WITH RECURSIVE seq AS (SELECT 1 AS n UNION ALL SELECT n + 1 FROM seq WHERE n < 200)
SELECT
    CONCAT('SKU-', LPAD(n, 5, '0')),
    CONCAT('Widget ', n),
    ELT(1 + (n % 4), 'Hardware', 'Software', 'Services', 'Consumables'),
    ELT(1 + (n % 3), 'A', 'B', 'C'),
    9.99 + (n * 2.1),
    3.00 + n,
    10 + (n * 3),
    5 + (n % 8),
    IF(n % 25 = 0, 1, 0)
FROM seq;

-- Reference skills
INSERT INTO skills (name, category) VALUES
('MySQL', 'data'),
('Python', 'dev'),
('Project Mgmt', 'soft'),
('Reporting', 'data'),
('API Design', 'dev'),
('Excel', 'office'),
('Security', 'ops'),
('Linux', 'ops'),
('Kubernetes', 'ops'),
('Salesforce', 'crm'),
('Negotiation', 'soft'),
('Statistics', 'data'),
('UI Design', 'ux'),
('QA Automation', 'qa'),
('ETL', 'data'),
('Docker', 'ops'),
('AWS', 'cloud'),
('TCP/IP', 'network'),
('SAML', 'security'),
('ETL Pipelines', 'data'),
('PowerShell', 'dev'),
('CI/CD', 'dev'),
('JIRA', 'office'),
('GDPR', 'compliance'),
('Risk Analysis', 'soft'),
('Data Modeling', 'data');

-- 400 assignments: each employee 1-200 two different projects
INSERT INTO assignments (employee_id, project_id, role_name, hours_allocated, assigned_date, billable)
WITH RECURSIVE nums AS (SELECT 1 n UNION ALL SELECT n + 1 FROM nums WHERE n < 200)
SELECT
    n,
    (n % 100) + 1,
    'Developer',
    40.00 + (n % 20),
    DATE_ADD('2023-01-01', INTERVAL n DAY),
    'Y'
FROM nums
UNION ALL
SELECT
    n,
    ((n + 17) % 100) + 1,
    'Analyst',
    35.00 + (n % 25),
    DATE_ADD('2023-02-15', INTERVAL n DAY),
    'Y'
FROM nums;

-- 300 employee skill rows: employees 1-150, two skills per employee
INSERT INTO employee_skills (employee_id, skill_id, proficiency, certified_date)
WITH RECURSIVE eids AS (SELECT 1 eid UNION ALL SELECT eid + 1 FROM eids WHERE eid < 150)
SELECT
    eid,
    ((eid * 2) % 25) + 1,
    1 + (eid % 5),
    DATE_ADD('2022-01-01', INTERVAL (eid % 200) DAY)
FROM eids
UNION ALL
SELECT
    eid,
    ((eid * 2 + 5) % 25) + 1,
    1 + (eid % 4),
    DATE_ADD('2022-06-15', INTERVAL (eid % 180) DAY)
FROM eids;

-- 300 orders
INSERT INTO orders (order_ref, customer_id, order_date, required_date, status, freight, sales_employee_id, notes)
WITH RECURSIVE seq AS (SELECT 1 AS n UNION ALL SELECT n + 1 FROM seq WHERE n < 300)
SELECT
    CONCAT('ORD-2024-', LPAD(n, 5, '0')),
    ((n - 1) % 150) + 1,
    DATE_ADD('2024-01-01', INTERVAL n DAY),
    DATE_ADD('2024-01-20', INTERVAL n DAY),
    ELT(1 + (n % 4), 'open', 'shipped', 'closed', 'cancelled'),
    5.00 + (n % 15),
    ((n * 3) % 200) + 1,
    IF(n % 6 = 0, CONCAT('Rush n', n), NULL)
FROM seq;

-- 900 line items: 3 lines per order
INSERT INTO order_line_items (order_id, line_no, product_id, quantity, unit_price, discount_pct)
WITH RECURSIVE seq AS (SELECT 1 AS n UNION ALL SELECT n + 1 FROM seq WHERE n < 900)
SELECT
    FLOOR((n - 1) / 3) + 1,
    1 + ((n - 1) % 3),
    ((n * 7) % 200) + 1,
    1.00 + (n % 8),
    20.00 + (n % 100),
    ((n * 3) % 5) * 2.0
FROM seq;
