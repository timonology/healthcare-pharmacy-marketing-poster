import Link from "next/link";
import { Button } from "@/components/ui/button";

export default function HomePage() {
  return (
    <main className="container flex min-h-screen flex-col items-center justify-center gap-6 py-20 text-center">
      <h1 className="text-4xl font-bold tracking-tight">Pharmacy Healthcare Poster</h1>
      <p className="max-w-prose text-muted-foreground">
        Welcome to Sonar Pharmacy Marketing Poster Service. 
        Here you can design Marketing Posters and send to customers. 
      </p>
      <div className="flex flex-wrap justify-center gap-3">
        <Button asChild>
          <Link href="/login">Sign in</Link>
        </Button>
        <Button asChild variant="secondary">
          <Link href="/register">Create account</Link>
        </Button>
        <Button asChild variant="outline">
          <Link href="/templates">Templates</Link>
        </Button>
        <Button asChild variant="outline">
          <Link href="/posters">My posters</Link>
        </Button>
        <Button asChild variant="outline">
          <Link href="/brand-kit">Brand kit</Link>
        </Button>
      </div>
    </main>
  );
}
