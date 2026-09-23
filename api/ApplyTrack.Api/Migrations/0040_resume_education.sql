-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- Education on the résumé (#277): schools, degrees, fields and dates, read from an uploaded
-- résumé's EDUCATION section. Forms that ask for education row by row (UKG's Education panel)
-- are filled from it; before it, those rows were always left for the person.

ALTER TABLE resume_profiles
    ADD COLUMN IF NOT EXISTS education jsonb NOT NULL DEFAULT '[]'::jsonb;
