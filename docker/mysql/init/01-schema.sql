-- Synthetic test schema for Db_Agent: larger, join-friendly, no real PII (ASCII only in seeds).
USE db_agent_test;

CREATE TABLE departments (
    id INT UNSIGNED NOT NULL AUTO_INCREMENT,
    name VARCHAR(100) NOT NULL,
    location VARCHAR(100) NOT NULL,
    cost_center VARCHAR(20) NULL,
    headcount_target SMALLINT UNSIGNED NULL,
    created_at DATE NOT NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uk_departments_name (name)
) ENGINE=InnoDB;

CREATE TABLE employees (
    id INT UNSIGNED NOT NULL AUTO_INCREMENT,
    department_id INT UNSIGNED NOT NULL,
    first_name VARCHAR(60) NOT NULL,
    last_name VARCHAR(60) NOT NULL,
    email VARCHAR(120) NOT NULL,
    phone_ext VARCHAR(8) NULL,
    job_title VARCHAR(100) NULL,
    hire_date DATE NOT NULL,
    emp_status VARCHAR(20) NOT NULL DEFAULT 'active',
    salary DECIMAL(12, 2) NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uk_employees_email (email),
    KEY ix_emp_dept (department_id),
    CONSTRAINT fk_employees_department
        FOREIGN KEY (department_id) REFERENCES departments (id)
) ENGINE=InnoDB;

CREATE TABLE projects (
    id INT UNSIGNED NOT NULL AUTO_INCREMENT,
    name VARCHAR(120) NOT NULL,
    start_date DATE NOT NULL,
    end_date DATE NULL,
    budget DECIMAL(14, 2) NOT NULL,
    priority TINYINT UNSIGNED NOT NULL DEFAULT 3,
    risk_level VARCHAR(20) NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uk_projects_name (name)
) ENGINE=InnoDB;

CREATE TABLE assignments (
    id INT UNSIGNED NOT NULL AUTO_INCREMENT,
    employee_id INT UNSIGNED NOT NULL,
    project_id INT UNSIGNED NOT NULL,
    role_name VARCHAR(80) NOT NULL,
    hours_allocated DECIMAL(8, 2) NOT NULL,
    assigned_date DATE NOT NULL,
    billable CHAR(1) NOT NULL DEFAULT 'Y',
    PRIMARY KEY (id),
    UNIQUE KEY uk_assignments_employee_project (employee_id, project_id),
    KEY ix_a_emp (employee_id),
    KEY ix_a_proj (project_id),
    CONSTRAINT fk_assignments_employee
        FOREIGN KEY (employee_id) REFERENCES employees (id),
    CONSTRAINT fk_assignments_project
        FOREIGN KEY (project_id) REFERENCES projects (id)
) ENGINE=InnoDB;

-- Sales / product domain (for multi-table and aggregate queries)
CREATE TABLE customers (
    id INT UNSIGNED NOT NULL AUTO_INCREMENT,
    code VARCHAR(20) NOT NULL,
    name VARCHAR(200) NOT NULL,
    country CHAR(2) NOT NULL,
    region VARCHAR(80) NULL,
    tier VARCHAR(20) NOT NULL,
    credit_limit DECIMAL(12, 2) NULL,
    created_at DATETIME NOT NULL,
    is_active TINYINT(1) NOT NULL DEFAULT 1,
    PRIMARY KEY (id),
    UNIQUE KEY uk_customers_code (code),
    KEY ix_cust_tier (tier),
    KEY ix_cust_country (country)
) ENGINE=InnoDB;

CREATE TABLE products (
    id INT UNSIGNED NOT NULL AUTO_INCREMENT,
    sku VARCHAR(32) NOT NULL,
    name VARCHAR(200) NOT NULL,
    category VARCHAR(80) NOT NULL,
    subcategory VARCHAR(80) NULL,
    list_price DECIMAL(12, 2) NOT NULL,
    cost DECIMAL(12, 2) NULL,
    stock_on_hand INT NOT NULL DEFAULT 0,
    reorder_level INT NOT NULL DEFAULT 0,
    is_discontinued TINYINT(1) NOT NULL DEFAULT 0,
    PRIMARY KEY (id),
    UNIQUE KEY uk_products_sku (sku),
    KEY ix_prod_cat (category, subcategory)
) ENGINE=InnoDB;

CREATE TABLE orders (
    id INT UNSIGNED NOT NULL AUTO_INCREMENT,
    order_ref VARCHAR(32) NOT NULL,
    customer_id INT UNSIGNED NOT NULL,
    order_date DATE NOT NULL,
    required_date DATE NULL,
    status VARCHAR(20) NOT NULL,
    freight DECIMAL(10, 2) NOT NULL DEFAULT 0,
    sales_employee_id INT UNSIGNED NULL,
    notes VARCHAR(500) NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uk_orders_ref (order_ref),
    KEY ix_o_cust (customer_id),
    KEY ix_o_date (order_date),
    KEY ix_o_status (status),
    CONSTRAINT fk_orders_customer FOREIGN KEY (customer_id) REFERENCES customers (id),
    CONSTRAINT fk_orders_emp FOREIGN KEY (sales_employee_id) REFERENCES employees (id)
) ENGINE=InnoDB;

CREATE TABLE order_line_items (
    id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    order_id INT UNSIGNED NOT NULL,
    line_no TINYINT UNSIGNED NOT NULL,
    product_id INT UNSIGNED NOT NULL,
    quantity DECIMAL(10, 2) NOT NULL,
    unit_price DECIMAL(12, 2) NOT NULL,
    discount_pct DECIMAL(5, 2) NOT NULL DEFAULT 0,
    PRIMARY KEY (id),
    UNIQUE KEY uk_oli (order_id, line_no),
    KEY ix_oli_product (product_id),
    CONSTRAINT fk_oli_order FOREIGN KEY (order_id) REFERENCES orders (id) ON DELETE CASCADE,
    CONSTRAINT fk_oli_product FOREIGN KEY (product_id) REFERENCES products (id)
) ENGINE=InnoDB;

CREATE TABLE skills (
    id INT UNSIGNED NOT NULL AUTO_INCREMENT,
    name VARCHAR(100) NOT NULL,
    category VARCHAR(60) NOT NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uk_skills_name (name)
) ENGINE=InnoDB;

CREATE TABLE employee_skills (
    id INT UNSIGNED NOT NULL AUTO_INCREMENT,
    employee_id INT UNSIGNED NOT NULL,
    skill_id INT UNSIGNED NOT NULL,
    proficiency TINYINT UNSIGNED NOT NULL,
    certified_date DATE NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uk_emp_skill (employee_id, skill_id),
    KEY ix_es_skill (skill_id),
    CONSTRAINT fk_es_emp FOREIGN KEY (employee_id) REFERENCES employees (id) ON DELETE CASCADE,
    CONSTRAINT fk_es_skill FOREIGN KEY (skill_id) REFERENCES skills (id)
) ENGINE=InnoDB;
