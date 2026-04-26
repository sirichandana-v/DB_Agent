-- Synthetic test schema for Db_Agent (no real PII).
USE db_agent_test;

CREATE TABLE departments (
    id INT UNSIGNED NOT NULL AUTO_INCREMENT,
    name VARCHAR(100) NOT NULL,
    location VARCHAR(100) NOT NULL,
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
    hire_date DATE NOT NULL,
    salary DECIMAL(12, 2) NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uk_employees_email (email),
    CONSTRAINT fk_employees_department
        FOREIGN KEY (department_id) REFERENCES departments (id)
) ENGINE=InnoDB;

CREATE TABLE projects (
    id INT UNSIGNED NOT NULL AUTO_INCREMENT,
    name VARCHAR(120) NOT NULL,
    start_date DATE NOT NULL,
    end_date DATE NULL,
    budget DECIMAL(14, 2) NOT NULL,
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
    PRIMARY KEY (id),
    UNIQUE KEY uk_assignments_employee_project (employee_id, project_id),
    CONSTRAINT fk_assignments_employee
        FOREIGN KEY (employee_id) REFERENCES employees (id),
    CONSTRAINT fk_assignments_project
        FOREIGN KEY (project_id) REFERENCES projects (id)
) ENGINE=InnoDB;
