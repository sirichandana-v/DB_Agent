-- Db_Agent — hard reasoning benchmarks (read-only SELECT / WITH).
-- For each: run the reference SQL in Adminer or `mysql`, then ask the app the English prompt
-- and compare the model's SQL / result to what you get here.

-- -----------------------------------------------------------------------------
-- English prompt (paste into BridgeWeb / CLI — keep it as one short ask)
-- -----------------------------------------------------------------------------
-- Among employees who have at least one skill in category "data" with proficiency
-- at least 3, which single department has the highest average salary? Return
-- department id, department name, and that average. If two departments tie on
-- average, pick the one with the smaller department id.
-- -----------------------------------------------------------------------------

-- Reference answer (one row; your exact floats may differ slightly on AVG)
WITH data_employees AS (
    SELECT DISTINCT es.employee_id
    FROM employee_skills es
    JOIN skills s ON s.id = es.skill_id
    WHERE s.category = 'data'
      AND es.proficiency >= 3
),
dept_salary AS (
    SELECT
        e.department_id,
        AVG(e.salary) AS avg_salary
    FROM employees e
    JOIN data_employees d ON d.employee_id = e.id
    WHERE e.salary IS NOT NULL
    GROUP BY e.department_id
)
SELECT
    d.id   AS department_id,
    d.name AS department_name,
    ds.avg_salary
FROM dept_salary ds
JOIN departments d ON d.id = ds.department_id
ORDER BY ds.avg_salary DESC, d.id ASC
LIMIT 1;

-- #############################################################################
-- BENCHMARK 2 — orders + line items + products (revenue, filters, GROUP BY)
-- #############################################################################

-- English prompt
-- -----------------------------------------------------------------------------
-- In calendar year 2024 only, consider orders whose status is not "cancelled".
-- For all matching order line items, which product "category" has the highest
-- total "line revenue", where line revenue = quantity * unit_price * (1 - discount_pct/100)
-- (treat discount_pct as a percent, e.g. 5 means 5%). Return the category and the
-- total revenue as one row. If two categories tie, return the one with the name
-- that sorts first alphabetically.
-- -----------------------------------------------------------------------------

-- Reference (one row; float formatting may differ)
SELECT
    p.category,
    SUM(
        oli.quantity * oli.unit_price * (1 - oli.discount_pct / 100.0)
    ) AS total_line_revenue
FROM order_line_items oli
JOIN orders o ON o.id = oli.order_id
JOIN products p ON p.id = oli.product_id
WHERE o.order_date >= '2024-01-01'
  AND o.order_date < '2025-01-01'
  AND o.status <> 'cancelled'
GROUP BY p.category
ORDER BY total_line_revenue DESC, p.category ASC
LIMIT 1;
