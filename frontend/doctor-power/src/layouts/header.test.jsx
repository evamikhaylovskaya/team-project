import { describe, it, expect } from "vitest";
import { render, screen } from "@testing-library/react";
import Header from "./header";

describe("Header", () => {
  it("renders the logo alt text", () => {
    render(<Header />);
    expect(screen.getByAltText("Doctor Power Logo")).toBeInTheDocument();
  });

  it("renders Login and Signin buttons", () => {
    render(<Header />);
    expect(screen.getByRole("button", { name: /Login/i })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Signin/i })).toBeInTheDocument();
  });

  it("shows tagline", () => {
    render(<Header />);
    expect(screen.getByText(/Power Platform Documentation Generator/i)).toBeInTheDocument();
  });
});
