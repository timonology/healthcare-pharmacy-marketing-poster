import { proxyDownload } from "@/lib/server/download-proxy";

/** The blank CSV showing the columns the importer expects. */
export const GET = () => proxyDownload("/api/patients/csv-template", "text/csv; charset=utf-8");
