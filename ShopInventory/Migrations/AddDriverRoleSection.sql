-- Migration: Add AssignedSection column for Driver role
-- Date: 2026-04-09
-- Description: Adds a section assignment field to the Users table for the new Driver role.
--              Sections: Cheeseman, Factory, Graniteside, Machipisa, Bulawayo

-- Add AssignedSection column to Users table
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'Users' AND column_name = 'AssignedSection'
    ) THEN
        ALTER TABLE "Users" ADD COLUMN "AssignedSection" VARCHAR(50) NULL;
    END IF;
END $$;
