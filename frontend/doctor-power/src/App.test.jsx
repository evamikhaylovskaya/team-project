import { describe, it, expect } from "vitest";
import { render, screen } from "@testing-library/react";
import App from "./App";

describe("App", () => {
  it("renders the app with header and main content", () => {
    render(<App />);
    expect(screen.getByRole("banner")).toBeInTheDocument();
    expect(screen.getByText(/Power Platform Documentation Generator/i)).toBeInTheDocument();
  });

  it("shows Upload File section on dashboard", () => {
    render(<App />);
    expect(screen.getByRole("heading", { name: /Upload File/i })).toBeInTheDocument();
  });
});
