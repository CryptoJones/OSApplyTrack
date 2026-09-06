-- SPDX-License-Identifier: Apache-2.0
-- Copyright 2026 Aaron K. Clark
-- Nearly every ATS form has a required résumé *file* field, but until now the
-- upload kept only the extracted text and discarded the bytes. Keep the PDF (max
-- 5 MB, enforced at upload) so the browser can attach it in memory at submit time.
-- Excluded from the account export, like cover letters.
ALTER TABLE resume_profiles
    ADD COLUMN IF NOT EXISTS source_pdf      bytea,
    ADD COLUMN IF NOT EXISTS source_pdf_name text NOT NULL DEFAULT '';
